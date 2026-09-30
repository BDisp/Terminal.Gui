// CoPilot - GPT-6
#nullable enable

using System.Reflection;
using Moq;

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
    public void UiThreadDispatch_AlsoRunsInlineForNonViewRunnable ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Mock<IRunnable> runnable = new ();
        runnable.SetupGet (instance => instance.IsRunning).Returns (true);
        PropertyInfo topProperty = typeof (ApplicationImpl).GetProperty (nameof (ApplicationImpl.TopRunnable))!;

        try
        {
            topProperty.SetValue (app, runnable.Object);
            bool ran = false;

            Task dispatch = app.InvokeAsync (() => ran = true, TestContext.Current.CancellationToken);

            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.True (ran);
        }
        finally
        {
            topProperty.SetValue (app, null);
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

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public async Task FinalSessionEndOrDispose_ResumesUiAwaiters (bool dispose)
    {
        for (int round = 0; round < 5; round++)
        {
            IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
            Runnable runnable = new ();
            SessionToken owner = app.Begin (runnable)!;
            using CountdownEvent queued = new (40);
            Task [] handlers = new Task [40];

            async Task Handler ()
            {
                try
                {
                    await Task.Run (async () =>
                    {
                        Task dispatch = app.InvokeAsync (owner, () => { });
                        queued.Signal ();
                        await dispatch.ConfigureAwait (false);
                    }, TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The session ended before the queued UI action started.
                }
            }

            try
            {
                for (int i = 0; i < handlers.Length; i++)
                {
                    handlers [i] = Handler ();
                }

                Assert.True (queued.Wait (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken));

                if (dispose)
                {
                    app.Dispose ();
                }
                else
                {
                    app.End (owner);
                }

                await Task.WhenAll (handlers).WaitAsync (TimeSpan.FromSeconds (5), TestContext.Current.CancellationToken);
                Assert.All (handlers, handler => Assert.True (handler.IsCompletedSuccessfully));
            }
            finally
            {
                runnable.Dispose ();
                app.Dispose ();
            }
        }
    }

    [Fact]
    public void OuterOwnedDispatch_RemainsValidWithNoCachedTopRunnable ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable outer = new ();
        Runnable inner = new ();
        SessionToken outerToken = app.Begin (outer)!;
        SessionToken innerToken = app.Begin (inner)!;
        app.End (innerToken);
        PropertyInfo topProperty = typeof (ApplicationImpl).GetProperty (nameof (ApplicationImpl.TopRunnable))!;
        IRunnable? savedTop = app.TopRunnable;

        try
        {
            topProperty.SetValue (app, null);
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (outerToken, () => { }));
            topProperty.SetValue (app, savedTop);
            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCompletedSuccessfully);
        }
        finally
        {
            topProperty.SetValue (app, savedTop);
            app.End (outerToken);
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void StoppedCachedTop_DoesNotMoveOuterContinuationOffUiThread ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable first = new ();
        app.End (app.Begin (first)!);
        first.Dispose ();

        Runnable outer = new ();
        Runnable inner = new ();
        SessionToken outerToken = app.Begin (outer)!;
        SessionToken innerToken = app.Begin (inner)!;
        app.End (innerToken);
        ApplicationImpl impl = (ApplicationImpl)app;
        PropertyInfo topProperty = typeof (ApplicationImpl).GetProperty (nameof (ApplicationImpl.TopRunnable))!;
        IRunnable? savedTop = app.TopRunnable;

        try
        {
            // Model the moment End has stopped the inner runnable but the cached top still points to it.
            topProperty.SetValue (app, inner);
            Assert.True (impl.HasRunningSession);
            Assert.True (impl.CanPumpPostedWork);

            int? callbackThreadId = null;
            Thread worker = new (() => impl.SynchronizationContext!.Post (
                                                                       _ => callbackThreadId = Thread.CurrentThread.ManagedThreadId,
                                                                       null));
            worker.Start ();
            worker.Join ();

            Assert.Null (callbackThreadId);
            app.TimedEvents!.RunTimers ();

            Assert.Equal (app.MainThreadId, callbackThreadId);
        }
        finally
        {
            topProperty.SetValue (app, savedTop);
            app.End (outerToken);
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void EndingNonTopSession_PreservesInnerOwnedDispatch ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable outer = new ();
        Runnable inner = new ();
        SessionToken outerToken = app.Begin (outer)!;
        SessionToken innerToken = app.Begin (inner)!;

        try
        {
            Task dispatch = QueueOnWorker (() => app.InvokeAsync (innerToken, () => { }));
            app.End (outerToken);
            app.TimedEvents!.RunTimers ();

            Assert.True (dispatch.IsCompletedSuccessfully);
            Assert.True (inner.IsRunning);
        }
        finally
        {
            app.End (innerToken);
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void ConcurrentSessionEnd_DoesNotRunUnstartedOwnedDispatches ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;
        const int count = 2_000;
        Task? [] dispatches = new Task? [count];
        using ManualResetEventSlim firstQueued = new ();
        int ran = 0;
        Exception? producerFailure = null;

        try
        {
            Thread producer = new (() =>
            {
                try
                {
                    for (int i = 0; i < dispatches.Length; i++)
                    {
                        dispatches [i] = app.InvokeAsync (owner, () => Interlocked.Increment (ref ran));

                        if (i == 0)
                        {
                            firstQueued.Set ();
                        }
                    }
                }
                catch (Exception ex)
                {
                    producerFailure = ex;
                    firstQueued.Set ();
                }
            });
            producer.Start ();
            Assert.True (firstQueued.Wait (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken));
            app.End (owner);
            producer.Join ();
            Assert.Null (producerFailure);

            app.TimedEvents!.RunTimers ();

            Assert.All (dispatches, dispatch => Assert.True (dispatch?.IsCanceled));
            Assert.Equal (0, ran);
            Assert.Empty (app.TimedEvents.Timeouts);
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void EndingSession_RemovesManyPendingDispatchesWithoutTouchingOtherTimeouts ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;

        try
        {
            object unrelated = app.AddTimeout (TimeSpan.FromHours (1), () => false)!;
            Task [] dispatches = new Task [500];
            Thread worker = new (() =>
            {
                for (int i = 0; i < dispatches.Length; i++)
                {
                    dispatches [i] = app.InvokeAsync (owner, () => { });
                }
            });
            worker.Start ();
            worker.Join ();

            app.End (owner);

            Assert.All (dispatches, dispatch => Assert.True (dispatch.IsCanceled));
            Assert.Single (app.TimedEvents!.Timeouts);
            Assert.True (app.TimedEvents.Remove (unrelated));
        }
        finally
        {
            app.End (owner);
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void BulkTimeoutRemoval_RemovesAllOccurrencesOfEachToken ()
    {
        TimedEvents timedEvents = new ();
        Terminal.Gui.App.Timeout repeated = new () { Span = TimeSpan.Zero, Callback = () => true };
        timedEvents.Add (repeated);
        timedEvents.Add (repeated);
        object unrelated = timedEvents.Add (TimeSpan.FromHours (1), () => false);

        timedEvents.RemoveMany ([repeated]);

        Assert.Single (timedEvents.Timeouts);
        Assert.True (timedEvents.Remove (unrelated));
    }

    [Fact]
    public void ConcurrentCancellationDuringQueueing_LeavesNoTimeouts ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        SessionToken owner = app.Begin (runnable)!;
        const int count = 5_000;
        Task [] dispatches = new Task [count];
        CancellationTokenSource [] sources = new CancellationTokenSource [count];
        int published = -1;

        try
        {
            for (int i = 0; i < count; i++)
            {
                sources [i] = new CancellationTokenSource ();
            }

            Thread canceller = new (() =>
            {
                for (int i = 0; i < count; i++)
                {
                    if (!SpinWait.SpinUntil (() => Volatile.Read (ref published) >= i, TimeSpan.FromSeconds (10)))
                    {
                        return;
                    }
                    sources [i].Cancel ();
                }
            });
            canceller.Start ();

            Thread producer = new (() =>
            {
                for (int i = 0; i < count; i++)
                {
                    Volatile.Write (ref published, i);
                    dispatches [i] = app.InvokeAsync (owner, () => { }, sources [i].Token);
                }
            });
            producer.Start ();
            producer.Join ();
            canceller.Join ();

            Assert.All (dispatches, dispatch => Assert.True (dispatch.IsCanceled));
            Assert.Empty (app.TimedEvents!.Timeouts);
        }
        finally
        {
            foreach (CancellationTokenSource source in sources)
            {
                source?.Dispose ();
            }

            app.End (owner);
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

    [Fact]
    public void ShutdownTakesPrecedenceOverAlreadyCanceledToken ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using CancellationTokenSource cancellation = new ();
        cancellation.Cancel ();
        FieldInfo stoppingField = typeof (ApplicationImpl).GetField ("_dispatchStopping", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            stoppingField.SetValue (app, true);

            Assert.Throws<NotInitializedException> (() => { _ = app.InvokeAsync (() => { }, cancellation.Token); });
        }
        finally
        {
            stoppingField.SetValue (app, false);
            app.Dispose ();
        }
    }

    [Fact]
    public void CustomApplicationCanProvideAwaitableDispatch ()
    {
        Mock<IApplication> app = new ();
        Mock<IApplicationAsyncDispatcher> dispatcher = app.As<IApplicationAsyncDispatcher> ();
        dispatcher.Setup (instance => instance.InvokeAsync (It.IsAny<Action<IApplication>> (), null, It.IsAny<CancellationToken> ()))
                  .Returns (Task.CompletedTask);

        Task dispatch = app.Object.InvokeAsync (() => { }, TestContext.Current.CancellationToken);

        Assert.True (dispatch.IsCompletedSuccessfully);
        dispatcher.Verify (instance => instance.InvokeAsync (It.IsAny<Action<IApplication>> (), null, TestContext.Current.CancellationToken), Times.Once);
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
