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

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled (cancellationToken);
        }

        UiDispatchOperation operation = new (this, action, owner, cancellationToken);

        lock (_dispatchLock)
        {
            if (!Initialized || _dispatchStopping)
            {
                throw new NotInitializedException (nameof (ApplicationDispatchExtensions.InvokeAsync));
            }

            if (owner is { })
            {
                if (owner.Runnable is null || SessionStack?.Contains (owner) != true)
                {
                    return Task.FromCanceled (new CancellationToken (true));
                }
            }
            else if (HasEndedSession && SessionStack?.Any (session => session.Runnable is { IsRunning: true }) != true)
            {
                return Task.FromCanceled (new CancellationToken (true));
            }

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

        try
        {
            object timeout = TimedEvents.Add (TimeSpan.Zero, () =>
            {
                operation.Execute ();

                return false;
            });
            operation.SetTimeout (timeout);
        }
        catch (Exception ex)
        {
            operation.Fail (ex);
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

    private void CancelOwnedDispatches (SessionToken owner)
    {
        UiDispatchOperation [] pending;

        lock (_dispatchLock)
        {
            pending = _pendingDispatches.Where (operation => ReferenceEquals (operation.Owner, owner)).ToArray ();
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
