namespace Terminal.Gui.App;

/// <summary>
///     Represents a running session created by <see cref="IApplication.Begin(IRunnable)"/>.
///     Wraps an <see cref="IRunnable"/> instance and is stored in <see cref="IApplication.SessionStack"/>.
/// </summary>
public class SessionToken
{
    internal SessionToken (IRunnable runnable) => Runnable = runnable;

    /// <summary>
    ///     Gets or sets the runnable associated with this session.
    ///     Set to <see langword="null"/> by <see cref="IApplication.End(SessionToken)"/> when the session completes.
    /// </summary>
    public IRunnable? Runnable { get; internal set; }

    private bool _isDispatchClosed;

    /// <summary>Whether this session has stopped accepting and running owned dispatches.</summary>
    internal bool IsDispatchClosed
    {
        get => Volatile.Read (ref _isDispatchClosed);
        set => Volatile.Write (ref _isDispatchClosed, value);
    }

    /// <summary>Whether owned dispatches may start: the session is running and has not begun to end.</summary>
    internal bool IsDispatchActive => !IsDispatchClosed && Runnable is { IsRunning: true };

    /// <summary>
    ///     The result of the session. Typically set by the runnable in <see langword="IRunnable.IsRunningChanged"/>
    /// </summary>
    public object? Result { get; set; }
}
