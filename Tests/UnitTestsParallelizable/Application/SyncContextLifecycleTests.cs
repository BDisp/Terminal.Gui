// Claude - Fable 5
#nullable enable

namespace ApplicationTests;

/// <summary>
///     Tests for the ambient <see cref="SynchronizationContext"/> contract across the application
///     lifecycle (#5636): <see cref="IApplication.Init"/> must not install the app's main-loop context
///     as the thread's ambient context (an <c>await</c> before <c>Run</c> would capture a context whose
///     continuations only run once the loop is pumping — a startup deadlock). The app context is
///     ambient only while a session is running, and the caller's context is restored afterwards.
/// </summary>
[Collection ("Application Tests")]
public class SyncContextLifecycleTests
{
    [Fact]
    public void Init_DoesNotChangeAmbientSynchronizationContext ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);

            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();

            Assert.Same (marker, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    [Fact]
    public void Run_SetsAppSyncContextDuringRun_AndRestoresAmbientAfter ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);

            SynchronizationContext? duringRun = null;

            void OnIteration (object? s, EventArgs<IApplication?> a)
            {
                duringRun = SynchronizationContext.Current;
                app.RequestStop ();
            }

            app.Iteration += OnIteration;

            using (Runnable runnable = new ())
            {
                app.Run (runnable);
            }

            app.Iteration -= OnIteration;

            Assert.Same (((ApplicationImpl)app).SynchronizationContext, duringRun);
            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // The documented Begin/End building-block sequence must restore the caller's ambient context
    // just like Run does — including nested sessions, where only the outermost End restores it.
    [Fact]
    public void Begin_End_RestoresAmbientSyncContext_IncludingNestedSessions ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);
            SynchronizationContext appContext = ((ApplicationImpl)app).SynchronizationContext!;

            using Runnable outer = new ();
            using Runnable inner = new ();

            SessionToken outerToken = app.Begin (outer)!;
            Assert.Same (appContext, SynchronizationContext.Current);

            SessionToken innerToken = app.Begin (inner)!;
            Assert.Same (appContext, SynchronizationContext.Current);

            app.End (innerToken);

            // The outer session is still active; the app context stays ambient.
            Assert.Same (appContext, SynchronizationContext.Current);

            app.End (outerToken);

            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void Begin_End_OuterEndedFirst_KeepsAppContextUntilLastSessionEnds ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);
            SynchronizationContext appContext = ((ApplicationImpl)app).SynchronizationContext!;

            using Runnable outer = new ();
            using Runnable inner = new ();

            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;

            app.End (outerToken);

            // The inner session is still running, so its awaits must keep resuming on the UI loop.
            Assert.Same (appContext, SynchronizationContext.Current);

            app.End (innerToken);

            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void End_WhenStoppedHandlerBeginsSession_KeepsAppContextUntilThatSessionEnds ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);
            SynchronizationContext appContext = ((ApplicationImpl)app).SynchronizationContext!;

            using Runnable first = new ();
            using Runnable next = new ();
            SessionToken? nextToken = null;

            SessionToken firstToken = app.Begin (first)!;
            first.IsRunningChanged += (_, args) =>
                                      {
                                          if (args.Value)
                                          {
                                              return;
                                          }

                                          nextToken = app.Begin (next);
                                      };

            app.End (firstToken);

            // The session begun while the last one was ending is running, so its awaits must resume on the UI loop.
            Assert.True (next.IsRunning);
            Assert.Same (appContext, SynchronizationContext.Current);

            app.End (nextToken!);

            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void Run_NestedInBegunSession_RestoresCallerContextWhenOuterEndedFirst ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);

            using Runnable outer = new ();
            using Runnable inner = new ();
            SessionToken outerToken = app.Begin (outer)!;

            // The outer session ends first, while the inner session is still stopping.
            inner.IsRunningChanging += (_, e) =>
                                       {
                                           if (e.NewValue)
                                           {
                                               return;
                                           }

                                           app.End (outerToken);
                                       };

            void OnIteration (object? s, EventArgs<IApplication?> a) => app.RequestStop ();

            app.Iteration += OnIteration;
            app.Run (inner);
            app.Iteration -= OnIteration;

            // No session is running, so Run must not reinstall the app context it found when it started.
            Assert.False (outer.IsRunning);
            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void Begin_AfterFinalSessionEndedOnWorker_StillRestoresCallerContext ()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext marker = new ();

        try
        {
            SynchronizationContext.SetSynchronizationContext (marker);

            IApplication app = Application.Create ();
            app.Init (DriverRegistry.Names.ANSI);
            SynchronizationContext appContext = ((ApplicationImpl)app).SynchronizationContext!;

            using Runnable first = new ();
            using Runnable second = new ();
            SessionToken firstToken = app.Begin (first)!;

            // A worker cannot change this thread's context, so the app context stays ambient here.
            Thread ender = new (() => app.End (firstToken));
            ender.Start ();
            Assert.True (ender.Join (TimeSpan.FromSeconds (10)));
            Assert.Same (appContext, SynchronizationContext.Current);

            SessionToken secondToken = app.Begin (second)!;
            app.End (secondToken);

            Assert.Same (marker, SynchronizationContext.Current);

            app.Dispose ();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext (previous);
        }
    }

    // A continuation that captured the app context during a session can resume after the session
    // ends; with no loop pumping (and the app still Initialized), it must not be stranded.
    [Fact]
    public void Post_AfterSessionEnded_StillExecutesCallback ()
    {
        IApplication app = Application.Create ();

        try
        {
            app.Init (DriverRegistry.Names.ANSI);

            void OnIteration (object? s, EventArgs<IApplication?> a) => app.RequestStop ();

            app.Iteration += OnIteration;

            using (Runnable runnable = new ())
            {
                app.Run (runnable);
            }

            app.Iteration -= OnIteration;

            ApplicationImpl impl = (ApplicationImpl)app;

            // After the session ends the loop is no longer a reliable pump; Post must
            // fall back to the thread pool (#5636). Ubuntu CI failed this at 2s under
            // parallel load. The pool can take longer than a tight Wait to inject a worker.
            Assert.False (impl.CanPumpPostedWork);

            SynchronizationContext context = impl.SynchronizationContext!;

            using ManualResetEventSlim callbackCalled = new (false);
            context.Post (_ => callbackCalled.Set (), null);

            Assert.True (
                         callbackCalled.Wait (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken),
                         "Post after session end must run on the thread pool, not wait for a loop that is no longer pumping.");
        }
        finally
        {
            app.Dispose ();
        }
    }

    [Fact]
    public void Post_AfterDispose_StillExecutesCallback ()
    {
        IApplication app = Application.Create ();
        app.Init (DriverRegistry.Names.ANSI);
        SynchronizationContext context = ((ApplicationImpl)app).SynchronizationContext!;
        app.Dispose ();

        // No main loop can ever pump this; the callback must not be stranded (or throw).
        using ManualResetEventSlim callbackCalled = new (false);
        context.Post (_ => callbackCalled.Set (), null);

        Assert.True (
                     callbackCalled.Wait (TimeSpan.FromSeconds (10), TestContext.Current.CancellationToken),
                     "Post after Dispose must run on the thread pool.");
    }
}
