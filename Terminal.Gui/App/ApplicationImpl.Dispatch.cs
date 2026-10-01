namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private readonly Lock _dispatchLock = new ();
    private readonly LinkedList<UiDispatchOperation> _queuedDispatches = new ();
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

        lock (_dispatchLock)
        {
            // Close the owner to dispatch before lifecycle events; keep Runnable available to their handlers.
            // Non-top ended tokens remain in SessionStack until the sessions above them end.
            owner.IsDispatchClosed = true;
            HasEndedSession = true;
            pending = HasRunningSession
                          ? _queuedDispatches.Where (operation => ReferenceEquals (operation.Owner, owner)).ToArray ()
                          : _queuedDispatches.ToArray ();
        }

        foreach (UiDispatchOperation operation in pending)
        {
            operation.Cancel ();
        }
    }

    private void StopDispatching ()
    {
        UiDispatchOperation [] pending;

        lock (_dispatchLock)
        {
            Volatile.Write (ref _dispatchStopping, true);
            pending = _queuedDispatches.ToArray ();
        }

        foreach (UiDispatchOperation operation in pending)
        {
            operation.Cancel ();
        }
    }
}
