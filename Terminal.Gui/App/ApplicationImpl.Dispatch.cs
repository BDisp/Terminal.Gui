namespace Terminal.Gui.App;

internal partial class ApplicationImpl
{
    private readonly Lock _dispatchLock = new ();
    private readonly HashSet<UiDispatchOperation> _pendingDispatches = [];
    private bool _dispatchStopping;

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

            if ((TopRunnable is null && HasEndedSession)
                || (owner is { } && (owner.Runnable is null || SessionStack?.Contains (owner) != true)))
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

        if (TopRunnableView is IRunnable { IsRunning: true } && MainThreadId == Thread.CurrentThread.ManagedThreadId)
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

        foreach (UiDispatchOperation operation in pending)
        {
            operation.Cancel ();
        }
    }

    private void CancelPendingDispatches (bool stopDispatching)
    {
        UiDispatchOperation [] pending;

        lock (_dispatchLock)
        {
            if (stopDispatching)
            {
                _dispatchStopping = true;
            }

            pending = _pendingDispatches.ToArray ();
        }

        foreach (UiDispatchOperation operation in pending)
        {
            operation.Cancel ();
        }
    }
}
