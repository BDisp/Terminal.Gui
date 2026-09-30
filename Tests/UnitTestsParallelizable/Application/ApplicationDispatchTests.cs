// Codex - GPT-6
#nullable enable

namespace ApplicationTests;

[Collection ("Application Tests")]
public class ApplicationDispatchTests
{
    [Fact]
    public void WorkerDispatch_CompletesOnlyAfterUiThreadRunsCallback ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            int? callbackThreadId = null;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => callbackThreadId = Thread.CurrentThread.ManagedThreadId));

            Assert.False (dispatch.IsCompleted, $"Dispatch status: {dispatch.Status}; main thread: {app.MainThreadId}; test thread: {Thread.CurrentThread.ManagedThreadId}; callback thread: {callbackThreadId}");
            Assert.Null (callbackThreadId);

            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.Equal (app.MainThreadId, callbackThreadId);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void UiThreadDispatch_CompletesImmediately ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            bool ran = false;
            Task dispatch = app.InvokeAsync (owner, instance =>
            {
                Assert.Same (app, instance);
                ran = true;
            }, TestContext.Current.CancellationToken);

            Assert.True (ran);
            Assert.True (dispatch.IsCompletedSuccessfully);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void CancellationBeforeQueue_DoesNotRunAction ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using CancellationTokenSource cancellation = new ();
        cancellation.Cancel ();

        try
        {
            bool ran = false;
            Task dispatch = app.InvokeAsync (() => ran = true, cancellation.Token);

            Assert.True (dispatch.IsCanceled);
            Assert.False (ran);
            Assert.Empty (app.TimedEvents!.Timeouts);
        }
        finally
        {
            app.Dispose ();
        }
    }

    [Fact]
    public void CancellationAfterQueue_RemovesCallback ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;
        using CancellationTokenSource cancellation = new ();

        try
        {
            bool ran = false;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => ran = true, cancellation.Token));
            Assert.False (dispatch.IsCompleted);

            cancellation.Cancel ();
            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCanceled);
            Assert.False (ran);
            Assert.Empty (app.TimedEvents!.Timeouts);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void CancellationFromEarlierTimeout_PreventsQueuedAction ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;
        using CancellationTokenSource cancellation = new ();

        try
        {
            bool ran = false;
            app.AddTimeout (TimeSpan.Zero, () =>
            {
                cancellation.Cancel ();

                return false;
            });
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => ran = true, cancellation.Token));

            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCanceled);
            Assert.False (ran);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void CancellationAfterActionStarts_DoesNotReplaceItsResult ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;
        using CancellationTokenSource cancellation = new ();

        try
        {
            Task dispatch = app.InvokeAsync (() => cancellation.Cancel (), cancellation.Token);

            Assert.True (dispatch.IsCompletedSuccessfully);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void EndingInnerSession_CancelsOnlyItsPendingDispatch ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable outer = new ();
        Runnable inner = new ();
        SessionToken outerToken = app.Begin (outer)!;
        SessionToken innerToken = app.Begin (inner)!;

        try
        {
            bool innerRan = false;
            bool outerRan = false;
            Task innerDispatch = QueueOnWorker (() => app.InvokeAsync (innerToken, () => innerRan = true));
            Task outerDispatch = QueueOnWorker (() => app.InvokeAsync (outerToken, () => outerRan = true));

            app.End (innerToken);
            app.TimedEvents!.RunTimers ();

            Assert.True (innerDispatch.IsCanceled);
            Assert.False (innerRan);
            Assert.True (outerDispatch.IsCompletedSuccessfully);
            Assert.True (outerRan);
        }
        finally
        {
            app.End (outerToken);
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void EndingFinalSession_CancelsApplicationDispatch ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            bool ran = false;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => ran = true));

            app.End (owner);

            Assert.True (dispatch.IsCanceled);
            Assert.False (ran);
            Assert.Empty (app.TimedEvents!.Timeouts);
            Assert.True (app.InvokeAsync (() => { }, TestContext.Current.CancellationToken).IsCanceled);
        }
        finally
        {
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void CanceledSessionEnd_DoesNotCancelOwnedDispatch ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        void VetoStop (object? _, CancelEventArgs<bool> args)
        {
            if (!args.NewValue)
            {
                args.Cancel = true;
            }
        }

        runnable.IsRunningChanging += VetoStop;

        try
        {
            bool ran = false;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (owner, () => ran = true));

            app.End (owner);
            app.TimedEvents!.RunTimers ();

            Assert.True (runnable.IsRunning);
            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.True (ran);
        }
        finally
        {
            runnable.IsRunningChanging -= VetoStop;
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void NewSessionAfterEarlierEnd_AllowsDispatchAgain ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable first = new ();
        Runnable second = new ();
        SessionToken firstToken = app.Begin (first)!;
        app.End (firstToken);

        try
        {
            SessionToken secondToken = app.Begin (second)!;
            bool ran = false;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (secondToken, () => ran = true));

            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.True (ran);
            app.End (secondToken);
        }
        finally
        {
            second.Dispose ();
            first.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void DisposeBeforeFirstSession_CancelsQueuedDispatch ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        bool ran = false;
        Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => ran = true));

        app.Dispose ();

        Assert.True (dispatch.IsCanceled);
        Assert.False (ran);
    }

    [Fact]
    public void QueueBeforeFirstSession_RunsAfterItStarts ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();

        try
        {
            bool ran = false;
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => ran = true));
            Assert.False (dispatch.IsCompleted);

            SessionToken owner = app.Begin (runnable)!;
            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.True (ran);
            app.End (owner);
        }
        finally
        {
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void UiThreadCallbackException_FaultsReturnedTask ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            InvalidOperationException failure = new ("callback failed");
            Task dispatch = app.InvokeAsync (() => throw failure, TestContext.Current.CancellationToken);

            Assert.Same (failure, Assert.Throws<InvalidOperationException> (() => dispatch.GetAwaiter ().GetResult ()));
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void CallbackException_FaultsTaskWithoutEscapingTimerPass ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            InvalidOperationException failure = new ("callback failed");
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (() => throw failure));

            app.TimedEvents!.RunTimers ();

            Assert.Same (failure, Assert.Throws<InvalidOperationException> (() => dispatch.GetAwaiter ().GetResult ()));
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void OwnerFromAnotherApplication_ReturnsCanceledTask ()
    {
        IApplication first = Application.Create ().Init (DriverRegistry.Names.ANSI);
        IApplication second = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = first.Begin (runnable)!;

        try
        {
            Assert.True (second.InvokeAsync (owner, () => { }, TestContext.Current.CancellationToken).IsCanceled);
        }
        finally
        {
            first.End (owner);
            runnable.Dispose ();
            second.Dispose ();
            first.Dispose ();
        }
    }

    [Fact]
    public void BeforeInitAndAfterDispose_ThrowNotInitialized ()
    {
        IApplication app = Application.Create ();

        Assert.Throws<NotInitializedException> (() => { _ = app.InvokeAsync (() => { }, TestContext.Current.CancellationToken); });

        app.Init (DriverRegistry.Names.ANSI);
        app.Dispose ();

        Assert.Throws<NotInitializedException> (() => { _ = app.InvokeAsync (() => { }, TestContext.Current.CancellationToken); });
    }

    private static Task QueueOnWorker (Func<Task> dispatch)
    {
        Task? queued = null;
        Exception? failure = null;
        Thread worker = new (() =>
        {
            try
            {
                queued = dispatch ();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        worker.Start ();
        worker.Join ();

        if (failure is { })
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture (failure).Throw ();
        }

        return queued!;
    }
}
