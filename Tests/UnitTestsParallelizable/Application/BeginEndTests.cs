using UnitTests;

namespace ApplicationTests.BeginEnd;

/// <summary>
///     Comprehensive tests for ApplicationImpl.Begin/End logic that manages Current and SessionStack.
///     These tests ensure the fragile state management logic is robust and catches regressions.
///     Tests work directly with ApplicationImpl instances to avoid global Application state issues.
/// </summary>
[Collection ("Application Tests")]
public class ApplicationImplBeginEndTests (ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;


    [Fact]
    public void Init_Begin_End_Cleans_Up ()
    {
        IApplication? app = Application.Create ();

        SessionToken? newSessionToken = null;

        EventHandler<SessionTokenEventArgs> newSessionTokenFn = (s, e) =>
                                                                {
                                                                    Assert.NotNull (e.State);
                                                                    newSessionToken = e.State;
                                                                };
        app.SessionBegun += newSessionTokenFn;

        Runnable<bool> runnable = new ();
        SessionToken sessionToken = app.Begin (runnable)!;
        Assert.NotNull (sessionToken);
        Assert.NotNull (newSessionToken);
        Assert.Equal (sessionToken, newSessionToken);

        // Assert.Equal (runnable, Application.TopRunnable);

        app.SessionBegun -= newSessionTokenFn;
        app.End (newSessionToken);

        Assert.Null (app.TopRunnable);
        Assert.Null (app.Driver);

        runnable.Dispose ();
    }

    [Fact]
    public void Begin_Null_Runnable_Throws ()
    {
        IApplication app = Application.Create ();
        app.Init (DriverRegistry.Names.ANSI);

        // Test null Runnable
        Assert.Throws<ArgumentNullException> (() => app.Begin (null!));

        app.Dispose ();
    }

    [Fact]
    public void Begin_Sets_Application_Top_To_Console_Size ()
    {
        IApplication app = Application.Create ();
        app.Init (DriverRegistry.Names.ANSI);

        Assert.Null (app.TopRunnableView);
        app.Driver!.SetScreenSize (80, 25);
        Runnable top = new ();
        SessionToken? token = app.Begin (top);
        Assert.Equal (new (0, 0, 80, 25), app.TopRunnableView!.Frame);
        app.Driver!.SetScreenSize (5, 5);
        app.LayoutAndDraw ();
        Assert.Equal (new (0, 0, 5, 5), app.TopRunnableView!.Frame);

        if (token is { })
        {
            app.End (token);
        }
        top.Dispose ();

        app.Dispose ();
    }

    [Fact]
    public void Begin_WithNullRunnable_ThrowsArgumentNullException ()
    {
        IApplication app = Application.Create ();

        try
        {
            Assert.Throws<ArgumentNullException> (() => app.Begin (null!));
        }
        finally
        {
            app.Dispose ();
        }
    }

    [Fact]
    public void Begin_SetsCurrent_WhenCurrentIsNull ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable = null;

        try
        {
            runnable = new ();
            Assert.Null (app.TopRunnableView);

            app.Begin (runnable);

            Assert.NotNull (app.TopRunnableView);
            Assert.Same (runnable, app.TopRunnableView);
            Assert.Single (app.SessionStack!);
        }
        finally
        {
            runnable?.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void Begin_PushesToSessionStack ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable1 = null;
        Runnable? runnable2 = null;

        try
        {
            runnable1 = new () { Id = "1" };
            runnable2 = new () { Id = "2" };

            app.Begin (runnable1);
            Assert.Single (app.SessionStack!);
            Assert.Same (runnable1, app.TopRunnableView);

            app.Begin (runnable2);
            Assert.Equal (2, app.SessionStack!.Count);
            Assert.Same (runnable2, app.TopRunnableView);
        }
        finally
        {
            runnable1?.Dispose ();
            runnable2?.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void End_WithNullSessionToken_ThrowsArgumentNullException ()
    {
        IApplication app = Application.Create ();

        try
        {
            Assert.Throws<ArgumentNullException> (() => app.End (null!));
        }
        finally
        {
            app.Dispose ();
        }
    }

    [Fact]
    public void End_PopsSessionStack ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable1 = null;
        Runnable? runnable2 = null;

        try
        {
            runnable1 = new () { Id = "1" };
            runnable2 = new () { Id = "2" };

            SessionToken token1 = app.Begin (runnable1)!;
            SessionToken token2 = app.Begin (runnable2)!;

            Assert.Equal (2, app.SessionStack!.Count);

            app.End (token2);

            Assert.Single (app.SessionStack!);
            Assert.Same (runnable1, app.TopRunnableView);

            app.End (token1);

            Assert.Empty (app.SessionStack!);
        }
        finally
        {
            runnable1?.Dispose ();
            runnable2?.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void End_RestoresCurrentToPreviousRunnable ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable1 = null;
        Runnable? runnable2 = null;
        Runnable? runnable3 = null;

        try
        {
            runnable1 = new () { Id = "1" };
            runnable2 = new () { Id = "2" };
            runnable3 = new () { Id = "3" };

            SessionToken? token1 = app.Begin (runnable1);
            SessionToken? token2 = app.Begin (runnable2);
            SessionToken? token3 = app.Begin (runnable3);

            Assert.Same (runnable3, app.TopRunnableView);

            app.End (token3!);
            Assert.Same (runnable2, app.TopRunnableView);

            app.End (token2!);
            Assert.Same (runnable1, app.TopRunnableView);

            app.End (token1!);
        }
        finally
        {
            runnable1?.Dispose ();
            runnable2?.Dispose ();
            runnable3?.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void MultipleBeginEnd_MaintainsStackIntegrity ()
    {
        IApplication app = Application.Create ();
        List<Runnable> runnables = new ();
        List<SessionToken> tokens = new ();

        try
        {
            // Begin multiple runnables
            for (var i = 0; i < 5; i++)
            {
                var runnable = new Runnable { Id = $"runnable-{i}" };
                runnables.Add (runnable);
                SessionToken? token = app.Begin (runnable);
                tokens.Add (token!);
            }

            Assert.Equal (5, app.SessionStack!.Count);
            Assert.Same (runnables [4], app.TopRunnableView);

            // End them in reverse order (LIFO)
            for (var i = 4; i >= 0; i--)
            {
                app.End (tokens [i]);

                if (i > 0)
                {
                    Assert.Equal (i, app.SessionStack.Count);
                    Assert.Same (runnables [i - 1], app.TopRunnableView);
                }
                else
                {
                    Assert.Empty (app.SessionStack);
                }
            }
        }
        finally
        {
            foreach (Runnable runnable in runnables)
            {
                runnable.Dispose ();
            }

            app.Dispose ();
        }
    }

    [Fact]
    public void End_NullsSessionTokenRunnable ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable = null;

        try
        {
            runnable = new ();

            SessionToken? token = app.Begin (runnable);
            Assert.Same (runnable, token!.Runnable);

            app.End (token);

            Assert.Null (token.Runnable);
        }
        finally
        {
            runnable?.Dispose ();
            app.Dispose ();
        }
    }

    [Fact]
    public void ResetState_ClearsSessionStack ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable1 = null;
        Runnable? runnable2 = null;

        try
        {
            runnable1 = new () { Id = "1" };
            runnable2 = new () { Id = "2" };

            app.Begin (runnable1);
            app.Begin (runnable2);

            Assert.Equal (2, app.SessionStack!.Count);
            Assert.NotNull (app.TopRunnableView);
        }
        finally
        {
            // Dispose runnables BEFORE Shutdown to satisfy DEBUG_IDISPOSABLE assertions
            runnable1?.Dispose ();
            runnable2?.Dispose ();

            // Shutdown calls ResetState, which will clear SessionStack and set Current to null
            app.Dispose ();

            // Verify cleanup happened
            Assert.Empty (app.SessionStack!);
            Assert.Null (app.TopRunnableView);
        }
    }

    [Fact]
    public void ResetState_StopsAllRunningRunnables ()
    {
        IApplication app = Application.Create ();
        Runnable? runnable1 = null;
        Runnable? runnable2 = null;

        try
        {
            runnable1 = new () { Id = "1" };
            runnable2 = new () { Id = "2" };

            app.Begin (runnable1);
            app.Begin (runnable2);

            Assert.True (runnable1.IsRunning);
            Assert.True (runnable2.IsRunning);
        }
        finally
        {
            // Dispose runnables BEFORE Shutdown to satisfy DEBUG_IDISPOSABLE assertions
            runnable1?.Dispose ();
            runnable2?.Dispose ();

            // Shutdown calls ResetState, which will stop all running runnables
            app.Dispose ();

            // Verify runnables were stopped
            Assert.False (runnable1!.IsRunning);
            Assert.False (runnable2!.IsRunning);
        }
    }

    //[Fact]
    //public void Begin_ActivatesNewRunnable_WhenCurrentExists ()
    //{
    //    IApplication app = Application.Create ();
    //    Runnable? runnable1 = null;
    //    Runnable? runnable2 = null;

    //    try
    //    {
    //        runnable1 = new () { Id = "1" };
    //        runnable2 = new () { Id = "2" };

    //        var runnable1Deactivated = false;
    //        var runnable2Activated = false;

    //        runnable1.Deactivate += (s, e) => runnable1Deactivated = true;
    //        runnable2.Activate += (s, e) => runnable2Activated = true;

    //        app.Begin (runnable1);
    //        app.Begin (runnable2);

    //        Assert.True (runnable1Deactivated);
    //        Assert.True (runnable2Activated);
    //        Assert.Same (runnable2, app.TopRunnable);
    //    }
    //    finally
    //    {
    //        runnable1?.Dispose ();
    //        runnable2?.Dispose ();
    //        app.Dispose ();
    //    }
    //}

    [Fact]
    public void SessionStack_ContainsAllBegunRunnables ()
    {
        IApplication app = Application.Create ();
        List<Runnable> runnables = new ();

        try
        {
            for (var i = 0; i < 10; i++)
            {
                var runnable = new Runnable { Id = $"runnable-{i}" };
                runnables.Add (runnable);
                app.Begin (runnable);
            }

            // All runnables should be in the stack
            Assert.Equal (10, app.SessionStack!.Count);

            // Verify stack contains all runnables
            List<SessionToken> stackList = app.SessionStack.ToList ();

            foreach (Runnable runnable in runnables)
            {
                Assert.Contains (runnable, stackList.Select (r => r.Runnable));
            }
        }
        finally
        {
            foreach (Runnable runnable in runnables)
            {
                runnable.Dispose ();
            }

            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void End_CalledConcurrentlyForSameSession_TearsDownOnce ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable outer = new ();
        Runnable inner = new ();
        using Barrier bothEnding = new (2);
        var endedCount = 0;
        EventHandler<SessionTokenEventArgs> onEnded = (_, _) => Interlocked.Increment (ref endedCount);

        try
        {
            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;

            // Hold both callers past End's unlocked check until each is ending the same session.
            inner.IsRunningChanging += (_, _) => bothEnding.SignalAndWait (TimeSpan.FromSeconds (10));
            app.SessionEnded += onEnded;

            Exception? [] errors = new Exception? [2];
            Thread [] enders = [StartEnder (0), StartEnder (1)];

            foreach (Thread ender in enders)
            {
                Assert.True (ender.Join (TimeSpan.FromSeconds (10)));
            }

            Assert.All (errors, Assert.Null);
            Assert.Equal (1, endedCount);
            Assert.False (inner.IsRunning);
            Assert.Same (outerToken, Assert.Single (app.SessionStack!));
            Assert.Same (outer, app.TopRunnable);
            Assert.True (outer.IsModal);
            app.End (outerToken);

            Thread StartEnder (int index)
            {
                Thread ender = new (() =>
                                    {
                                        try
                                        {
                                            app.End (innerToken);
                                        }
                                        catch (Exception exception)
                                        {
                                            errors [index] = exception;
                                        }
                                    });
                ender.Start ();

                return ender;
            }
        }
        finally
        {
            app.SessionEnded -= onEnded;
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void End_CalledFromOwnIsRunningChangingHandler_TearsDownOnce ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable runnable = new ();
        var endedCount = 0;
        var stoppedCount = 0;
        EventHandler<SessionTokenEventArgs> onEnded = (_, _) => endedCount++;

        try
        {
            SessionToken token = app.Begin (runnable)!;
            var reentered = false;

            runnable.IsRunningChanging += (_, _) =>
                                          {
                                              if (reentered)
                                              {
                                                  return;
                                              }

                                              reentered = true;
                                              app.End (token);
                                          };
            runnable.IsRunningChanged += (_, args) => stoppedCount += args.Value ? 0 : 1;
            app.SessionEnded += onEnded;

            app.End (token);

            Assert.Equal (1, endedCount);
            Assert.Equal (1, stoppedCount);
            Assert.Empty (app.SessionStack!);
            Assert.Null (app.TopRunnable);
        }
        finally
        {
            app.SessionEnded -= onEnded;
            runnable.Dispose ();
            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void End_OfModalSessionBeneathTop_DoesNotPopTop ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        Runnable outer = new ();
        Runnable inner = new ();

        try
        {
            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;

            // Leave the outer session modal beneath the top, as a Begin racing End's modal read would.
            outer.SetIsModal (true);

            app.End (outerToken);

            Assert.False (outer.IsRunning);
            Assert.Same (innerToken, app.SessionStack!.First ());
            Assert.Same (inner, app.TopRunnable);
            Assert.True (inner.IsModal);
            app.End (innerToken);
            Assert.Empty (app.SessionStack!);
        }
        finally
        {
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void LayoutAndDraw_AfterNonTopSessionEnded_DrawsOnlyRunningSessions ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize (10, 3);
        Runnable outer = new () { Text = "OOOOOOOOOO\nOOOOOOOOOO\nOOOOOOOOOO" };
        Runnable inner = new () { X = 2, Y = 1, Width = 5, Height = 1, Text = "inner" };

        try
        {
            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;
            app.LayoutAndDraw ();
            DriverAssert.AssertDriverContentsAre ("OOOOOOOOOO\nOOinnerOOO\nOOOOOOOOOO", _output, app.Driver);

            app.End (outerToken);
            app.LayoutAndDraw ();

            DriverAssert.AssertDriverContentsAre ("inner", _output, app.Driver);
            app.End (innerToken);
        }
        finally
        {
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void GetViewsUnderLocation_AfterNonTopSessionEnded_SkipsEndedSession ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize (10, 3);
        Runnable outer = new ();
        Runnable inner = new () { X = 2, Y = 1, Width = 5, Height = 1 };

        try
        {
            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;
            app.End (outerToken);

            Assert.Empty (inner.GetViewsUnderLocation (new Point (0, 0), ViewportSettingsFlags.None));
            Assert.Contains (inner, inner.GetViewsUnderLocation (new Point (3, 1), ViewportSettingsFlags.None));
            app.End (innerToken);
        }
        finally
        {
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }

    // Claude - Opus 5.5
    [Fact]
    public void MovingLastRunningSession_AfterNonTopSessionEnded_ClearsOldCells ()
    {
        IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize (10, 3);
        Runnable outer = new ();
        Runnable inner = new () { X = 2, Y = 1, Width = 5, Height = 1, Text = "inner" };

        try
        {
            SessionToken outerToken = app.Begin (outer)!;
            SessionToken innerToken = app.Begin (inner)!;
            app.End (outerToken);
            app.LayoutAndDraw ();

            inner.X = 4;
            app.LayoutAndDraw ();

            Assert.Equal (4, inner.Frame.X);
            DriverAssert.AssertDriverContentsAre ("inner", _output, app.Driver);
            app.End (innerToken);
        }
        finally
        {
            inner.Dispose ();
            outer.Dispose ();
            app.Dispose ();
        }
    }
}
