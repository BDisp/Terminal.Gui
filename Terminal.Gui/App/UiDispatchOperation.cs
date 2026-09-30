namespace Terminal.Gui.App;

internal sealed class UiDispatchOperation
{
    private readonly Action<IApplication> _action;
    private readonly ApplicationImpl _app;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource _completion = new (TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _registrationLock = new ();
    private CancellationTokenRegistration _registration;
    private object? _timeout;
    private int _state;

    internal UiDispatchOperation (ApplicationImpl app, Action<IApplication> action, SessionToken? owner, CancellationToken cancellationToken)
    {
        _app = app;
        _action = action;
        Owner = owner;
        _cancellationToken = cancellationToken;
    }

    internal SessionToken? Owner { get; }

    internal Task Task => _completion.Task;

    internal bool IsPending => Volatile.Read (ref _state) == 0;

    internal bool TryStart () => Interlocked.CompareExchange (ref _state, 1, 0) == 0;

    internal void RegisterCancellation ()
    {
        if (!_cancellationToken.CanBeCanceled)
        {
            return;
        }

        CancellationTokenRegistration registration = _cancellationToken.Register (static state => ((UiDispatchOperation)state!).Cancel (), this);

        lock (_registrationLock)
        {
            if (IsPending)
            {
                _registration = registration;

                return;
            }
        }

        registration.Unregister ();
    }

    internal void SetTimeout (object timeout)
    {
        Interlocked.Exchange (ref _timeout, timeout);

        if (!IsPending)
        {
            _app.TimedEvents.Remove (timeout);
        }
    }

    internal void Execute ()
    {
        if (_cancellationToken.IsCancellationRequested || !_app.TryStartDispatch (this))
        {
            Cancel ();

            return;
        }

        try
        {
            _action (_app);
            _completion.TrySetResult ();
        }
        catch (OperationCanceledException ex) when (_cancellationToken.IsCancellationRequested && ex.CancellationToken == _cancellationToken)
        {
            _completion.TrySetCanceled (_cancellationToken);
        }
        catch (Exception ex)
        {
            _completion.TrySetException (ex);
        }
        finally
        {
            Volatile.Write (ref _state, 2);
            UnregisterCancellation ();
            _app.CompleteDispatch (this);
        }
    }

    internal void Cancel () => CancelCore (true);

    internal object? CancelForBulk () => CancelCore (false);

    private object? CancelCore (bool removeTimeout)
    {
        if (Interlocked.CompareExchange (ref _state, 2, 0) != 0)
        {
            return null;
        }

        if (_cancellationToken.IsCancellationRequested)
        {
            _completion.TrySetCanceled (_cancellationToken);
        }
        else
        {
            _completion.TrySetCanceled ();
        }

        object? timeout = Volatile.Read (ref _timeout);

        if (removeTimeout && timeout is { })
        {
            _app.TimedEvents.Remove (timeout);
        }

        UnregisterCancellation ();
        _app.CompleteDispatch (this);

        return timeout;
    }

    internal void Fail (Exception exception)
    {
        if (Interlocked.CompareExchange (ref _state, 2, 0) != 0)
        {
            return;
        }

        _completion.TrySetException (exception);
        UnregisterCancellation ();
        _app.CompleteDispatch (this);
    }

    private void UnregisterCancellation ()
    {
        CancellationTokenRegistration registration;

        lock (_registrationLock)
        {
            registration = _registration;
            _registration = default;
        }

        registration.Unregister ();
    }
}
