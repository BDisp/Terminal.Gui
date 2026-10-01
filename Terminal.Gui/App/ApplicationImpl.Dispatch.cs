namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private readonly Lock _dispatchLock = new ();
    private readonly LinkedList<UiDispatchOperation> _queuedDispatches = new ();
    private readonly LinkedList<PostedCallback> _postedCallbacks = new ();
    private bool _dispatchStopping;

    Task IApplicationAsyncDispatcher.InvokeAsync (Action<IApplication> action, SessionToken? owner, CancellationToken cancellationToken) =>
        InvokeAsyncCore (action, owner, cancellationToken);

    internal Task InvokeAsyncCore (Action<IApplication> action, SessionToken? owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull (action);

        if (!Initialized)
        {
            throw new NotInitializedException (nameof (ApplicationDispatchExtensions.InvokeAsync));
        }

        UiDispatchOperation operation;

        lock (_dispatchLock)
        {
            if (!Initialized || _dispatchStopping)
            {
                throw new NotInitializedException (nameof (ApplicationDispatchExtensions.InvokeAsync));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled (cancellationToken);
            }

            if (owner is { })
            {
                if (owner.IsDispatchClosed || owner.Runnable is null || SessionStack?.Contains (owner) != true)
                {
                    return Task.FromCanceled (new CancellationToken (true));
                }
            }
            else if (HasEndedSession && !HasRunningSession)
            {
                return Task.FromCanceled (new CancellationToken (true));
            }

            operation = new (this, action, owner, cancellationToken);
            operation.QueueNode = _queuedDispatches.AddLast (operation);
        }

        operation.RegisterCancellation ();

        if (!operation.IsPending)
        {
            return operation.Task;
        }

        if (MainThreadId == Thread.CurrentThread.ManagedThreadId && HasRunningSession)
        {
            operation.Execute ();
        }

        return operation.Task;
    }

    internal int QueuedDispatchCount
    {
        get
        {
            lock (_dispatchLock)
            {
                return _queuedDispatches.Count;
            }
        }
    }

    internal void DrainDispatches ()
    {
        if (MainThreadId != Thread.CurrentThread.ManagedThreadId || !HasRunningSession)
        {
            return;
        }

        UiDispatchOperation [] dispatches;

        lock (_dispatchLock)
        {
            // Leave queued operations linked until each starts so End can cancel them during a reentrant callback.
            dispatches = _queuedDispatches.ToArray ();
        }

        foreach (UiDispatchOperation operation in dispatches)
        {
            operation.Execute ();
        }
    }

    internal void CompleteDispatch (UiDispatchOperation operation)
    {
        lock (_dispatchLock)
        {
            if (operation.QueueNode is not { } node)
            {
                return;
            }

            _queuedDispatches.Remove (node);
            operation.QueueNode = null;
        }
    }

    internal bool TryStartDispatch (UiDispatchOperation operation)
    {
        lock (_dispatchLock)
        {
            if (MainThreadId != Thread.CurrentThread.ManagedThreadId
                || _dispatchStopping
                || operation.Owner is { IsDispatchClosed: true } or { Runnable: null }
                || (operation.Owner is null && HasEndedSession && !HasRunningSession))
            {
                return false;
            }

            if (!operation.TryStart ())
            {
                return false;
            }

            if (operation.QueueNode is { } node)
            {
                _queuedDispatches.Remove (node);
                operation.QueueNode = null;
            }

            return true;
        }
    }

    private void EndSessionDispatches (SessionToken owner)
    {
        UiDispatchOperation [] pending;
        PostedCallback [] posted = [];

        lock (_dispatchLock)
        {
            // Close the owner to dispatch before lifecycle events; keep Runnable available to their handlers.
            // Non-top ended tokens remain in SessionStack until the sessions above them end.
            owner.IsDispatchClosed = true;
            HasEndedSession = true;

            if (HasRunningSession)
            {
                pending = _queuedDispatches.Where (operation => ReferenceEquals (operation.Owner, owner)).ToArray ();
            }
            else
            {
                pending = [.. _queuedDispatches];
                posted = TakePostedCallbacks ();
            }
        }

        CancelAndRelease (pending, posted);
    }

    private void StopDispatching ()
    {
        UiDispatchOperation [] pending;
        PostedCallback [] posted;

        lock (_dispatchLock)
        {
            Volatile.Write (ref _dispatchStopping, true);
            pending = [.. _queuedDispatches];
            posted = TakePostedCallbacks ();
        }

        CancelAndRelease (pending, posted);
    }

    private static void CancelAndRelease (UiDispatchOperation [] pending, PostedCallback [] posted)
    {
        foreach (UiDispatchOperation operation in pending)
        {
            operation.Cancel ();
        }

        foreach (PostedCallback callback in posted)
        {
            RunOnThreadPool (callback.Callback, callback.State);
        }
    }

    /// <summary>
    ///     INTERNAL: Runs <see cref="MainLoopSyncContext"/> work on the main loop. Work posted while the loop can pump
    ///     is tracked until it runs; if the loop stops first, it moves to the thread pool so awaiters are not
    ///     stranded (#5636).
    /// </summary>
    internal void PostToMainLoop (SendOrPostCallback callback, object? state)
    {
        LinkedListNode<PostedCallback>? posted = null;

        lock (_dispatchLock)
        {
            if (CanPumpPostedWork)
            {
                posted = _postedCallbacks.AddLast (new PostedCallback (callback, state));
            }
        }

        if (posted is null)
        {
            RunOnThreadPool (callback, state);

            return;
        }

        // Like Invoke: run inline on the running UI thread; otherwise queue for the main loop.
        if (TopRunnableView is IRunnable { IsRunning: true } && MainThreadId == Thread.CurrentThread.ManagedThreadId)
        {
            RunPosted (posted);

            return;
        }

        TimedEvents.Add (TimeSpan.Zero,
                         () =>
                         {
                             RunPosted (posted);

                             return false;
                         });
    }

    private void RunPosted (LinkedListNode<PostedCallback> posted)
    {
        lock (_dispatchLock)
        {
            // A cleared node already moved to the thread pool when the loop stopped.
            if (posted.List is null)
            {
                return;
            }

            _postedCallbacks.Remove (posted);
        }

        posted.Value.Callback (posted.Value.State);
    }

    // Caller must hold _dispatchLock.
    private PostedCallback [] TakePostedCallbacks ()
    {
        PostedCallback [] posted = [.. _postedCallbacks];
        _postedCallbacks.Clear ();

        return posted;
    }

    internal static void RunOnThreadPool (SendOrPostCallback callback, object? state) =>
        ThreadPool.QueueUserWorkItem (static posted => posted.Callback (posted.State), new PostedCallback (callback, state), false);

    private readonly record struct PostedCallback (SendOrPostCallback Callback, object? State);
}
