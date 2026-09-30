namespace Terminal.Gui.App;

internal sealed class UiDispatchOperation
{
    private readonly Action<IApplication> _action;
    private readonly ApplicationImpl _app;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource _completion = new (TaskCreationOptions.RunContinuationsAsynchronously);
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

    internal void RegisterCancellation ()
    {
        if (!_cancellationToken.CanBeCanceled)
        {
            return;
        }

        _registration = _cancellationToken.Register (static state => ((UiDispatchOperation)state!).Cancel (), this);

        if (!IsPending)
        {
            _registration.Unregister ();
        }
    }

    internal void SetTimeout (object timeout)
    {
        Volatile.Write (ref _timeout, timeout);

        if (!IsPending)
        {
            _app.TimedEvents.Remove (timeout);
        }
    }

    internal void Execute ()
    {
        if (_cancellationToken.IsCancellationRequested || Owner is { Runnable: null })
        {
            Cancel ();

            return;
        }

        if (Interlocked.CompareExchange (ref _state, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _action (_app);
            _completion.TrySetResult ();
        }
        catch (Exception ex)
        {
            _completion.TrySetException (ex);
        }
        finally
        {
            Volatile.Write (ref _state, 2);
            _registration.Unregister ();
            _app.CompleteDispatch (this);
        }
    }

    internal void Cancel ()
    {
        if (Interlocked.CompareExchange (ref _state, 2, 0) != 0)
        {
            return;
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

        if (timeout is { })
        {
            _app.TimedEvents.Remove (timeout);
        }

        _registration.Unregister ();
        _app.CompleteDispatch (this);
    }

    internal void Fail (Exception exception)
    {
        if (Interlocked.CompareExchange (ref _state, 2, 0) != 0)
        {
            return;
        }

        _completion.TrySetException (exception);
        _registration.Unregister ();
        _app.CompleteDispatch (this);
    }
}
