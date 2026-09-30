namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private readonly Lock _dispatchLock = new ();
    private readonly HashSet<UiDispatchOperation> _pendingDispatches = [];
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
            _pendingDispatches.Add (operation);
        }

        operation.RegisterCancellation ();

        if (!operation.IsPending)
        {
            return operation.Task;
        }

        if (TopRunnable is { IsRunning: true } && MainThreadId == Thread.CurrentThread.ManagedThreadId)
        {
            operation.Execute ();

            return operation.Task;
        }

        Timeout timeout = new ()
        {
            Span = TimeSpan.Zero,
            Callback = () =>
            {
                operation.Execute ();

                return false;
            }
        };

        try
        {
            object timeoutToken = TimedEvents.Add (timeout);
            operation.SetTimeout (timeoutToken);
        }
        catch (Exception ex)
        {
            Exception failure = ex;

            try
            {
                // Added handlers run after insertion and can throw before Add returns its token.
                TimedEvents.Remove (timeout);
            }
            catch (Exception cleanupEx)
            {
                failure = new AggregateException (ex, cleanupEx);
            }

            operation.Fail (failure);
        }

        return operation.Task;
    }

    internal void CompleteDispatch (UiDispatchOperation operation)
    {
        lock (_dispatchLock)
        {
            _pendingDispatches.Remove (operation);
        }
    }

    internal bool TryStartDispatch (UiDispatchOperation operation)
    {
        lock (_dispatchLock)
        {
            if (_dispatchStopping
                || operation.Owner is { IsDispatchClosed: true } or { Runnable: null }
                || (operation.Owner is null && HasEndedSession && !HasRunningSession))
            {
                return false;
            }

            return operation.TryStart ();
        }
    }

    private void EndSessionDispatches (SessionToken owner)
    {
        UiDispatchOperation [] pending;

        lock (_dispatchLock)
        {
            // Close the owner to dispatch before lifecycle events; keep Runnable available to their handlers.
            // Non-top ended tokens can remain in SessionStack.
            owner.IsDispatchClosed = true;
            HasEndedSession = true;
            pending = HasRunningSession
                          ? _pendingDispatches.Where (operation => ReferenceEquals (operation.Owner, owner)).ToArray ()
                          : _pendingDispatches.ToArray ();
        }

        CancelDispatches (pending);
    }

    private void CancelPendingDispatches (bool stopDispatching)
    {
        UiDispatchOperation [] pending;

        lock (_dispatchLock)
        {
            if (stopDispatching)
            {
                Volatile.Write (ref _dispatchStopping, true);
            }

            pending = _pendingDispatches.ToArray ();
        }

        CancelDispatches (pending);
    }

    private void CancelDispatches (UiDispatchOperation [] pending)
    {
        List<object> timeouts = [];

        foreach (UiDispatchOperation operation in pending)
        {
            if (operation.CancelForBulk () is { } timeout)
            {
                timeouts.Add (timeout);
            }
        }

        if (TimedEvents is TimedEvents timedEvents)
        {
            timedEvents.RemoveMany (timeouts);

            return;
        }

        foreach (object timeout in timeouts)
        {
            TimedEvents.Remove (timeout);
        }
    }
}
