using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Moq;

namespace ApplicationTests.MouseTests;

// CoPilot - GPT-6
[Collection ("Application Tests")]
public class MouseReportingTests
{
    private static ApplicationImpl Create (StringBuilder bytes, bool disabled = false, AppModel model = AppModel.Inline, ITimeProvider? timeProvider = null)
    {
        AnsiOutput output = new (chunk =>
                                 {
                                     bytes.Append (Encoding.UTF8.GetString (chunk));

                                     return true;
                                 },
                                 model);
        output.SetSize (80, 12);
        AnsiComponentFactory factory = new (output: output);
        ApplicationImpl app = new (factory, timeProvider ?? new SystemTimeProvider ()) { AppModel = model, ForceInlinePosition = Point.Empty };
        app.Mouse.IsMouseDisabled = disabled;
        app.Init ();
        Assert.Equal (disabled, factory.IsMouseDisabled);
        Assert.Equal (disabled, output.IsMouseDisabled);
        return app;
    }

    [Theory]
    [InlineData (AppModel.Inline)]
    [InlineData (AppModel.FullScreen)]
    public void PreInitSetting_ReachesAnsiFactoryAndOutput (AppModel model)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, true, model);

        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.Contains (EscSeqUtils.CSI_DisableMouseEvents, bytes.ToString ());
        Assert.DoesNotContain (EscSeqUtils.CSI_EnableMouseEvents, bytes.ToString ());
    }

    [Fact]
    public void RuntimeToggle_ChangesReportingOncePerTransition ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes);
        bytes.Clear ();

        app.Mouse.IsMouseDisabled = true;
        app.Mouse.IsMouseDisabled = true;
        app.Mouse.IsMouseDisabled = false;
        app.Mouse.IsMouseDisabled = false;

        Assert.Equal (EscSeqUtils.CSI_DisableMouseEvents + EscSeqUtils.CSI_EnableMouseEvents, bytes.ToString ());
    }

    [Fact]
    public void FailedModeWrite_DoesNotPublishDisabledStateAndCanBeRetried ()
    {
        bool fail = false;
        AnsiOutput output = new (_ => !fail, AppModel.Inline);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline };
        app.Init ();
        fail = true;

        Assert.Throws<IOException> (() => app.Mouse.IsMouseDisabled = true);
        Assert.False (app.Mouse.IsMouseDisabled);
        Assert.False (output.IsMouseDisabled);

        fail = false;
        app.Mouse.IsMouseDisabled = true;
        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.True (output.IsMouseDisabled);
    }

    [Fact]
    public void RuntimeToggle_WaitsForPhysicalInsertionAndRedraw ()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using ManualResetEventSlim insertionStarted = new ();
        using ManualResetEventSlim releaseInsertion = new ();
        using ManualResetEventSlim changeRequested = new ();
        using ManualResetEventSlim changeFinished = new ();
        ConcurrentQueue<string> writes = new ();
        bool pause = false;
        AnsiOutput output = new (chunk =>
        {
            writes.Enqueue (Encoding.UTF8.GetString (chunk));
            if (pause)
            {
                insertionStarted.Set ();
                Assert.True (releaseInsertion.Wait (TimeSpan.FromSeconds (5), cancellationToken));
            }

            return true;
        }, AppModel.Inline);
        output.SetSize (80, 12);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = Point.Empty };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Task print = app.PrintAboveAsync (owner, "finished", cancellationToken);
        pause = true;
        Exception? renderFailure = null;
        Exception? changeFailure = null;
        int? mainThreadId = app.MainThreadId;
        Thread render = new (() =>
        {
            try
            {
                app.MainThreadId = Thread.CurrentThread.ManagedThreadId;
                app.LayoutAndDraw ();
            }
            catch (Exception error) { renderFailure = error; }
        })
        { IsBackground = true };
        Thread change = new (() =>
        {
            try
            {
                Assert.True (insertionStarted.Wait (TimeSpan.FromSeconds (5), cancellationToken));
                changeRequested.Set ();
                app.Mouse.IsMouseDisabled = true;
            }
            catch (Exception error) { changeFailure = error; }
            finally { changeFinished.Set (); }
        })
        { IsBackground = true };
        render.Start ();
        change.Start ();
        try
        {
            Assert.True (insertionStarted.Wait (TimeSpan.FromSeconds (5), cancellationToken));
            Assert.True (changeRequested.Wait (TimeSpan.FromSeconds (5), cancellationToken));
            Assert.False (changeFinished.Wait (TimeSpan.FromMilliseconds (100), cancellationToken));
        }
        finally
        {
            releaseInsertion.Set ();
            Assert.True (render.Join (TimeSpan.FromSeconds (5)));
            Assert.True (change.Join (TimeSpan.FromSeconds (5)));
            app.MainThreadId = mainThreadId;
        }

        Assert.Null (renderFailure);
        Assert.Null (changeFailure);
        Assert.True (print.IsCompletedSuccessfully);
        Assert.Equal (EscSeqUtils.CSI_DisableMouseEvents, writes.Last ());
        app.End (owner);
    }

    [Fact]
    public void RuntimeToggle_OnlyChangesItsOwnApplication ()
    {
        StringBuilder firstBytes = new ();
        StringBuilder secondBytes = new ();
        using ApplicationImpl first = Create (firstBytes);
        using ApplicationImpl second = Create (secondBytes);
        firstBytes.Clear ();
        secondBytes.Clear ();

        first.Mouse.IsMouseDisabled = true;

        Assert.Equal (EscSeqUtils.CSI_DisableMouseEvents, firstBytes.ToString ());
        Assert.Empty (secondBytes.ToString ());
        Assert.False (second.Mouse.IsMouseDisabled);
    }

    [Fact]
    public void DisabledMouse_KeepsKeyboardAndPrintAboveWorking ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, true);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        TextField input = new () { Width = Dim.Fill () };
        composer.Add (input);
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        bool mouseReceived = false;
        app.Mouse.MouseEvent += (_, _) => mouseReceived = true;

        app.InjectMouse (new Mouse { ScreenPosition = Point.Empty, Flags = MouseFlags.LeftButtonClicked });
        app.InjectKey (Key.A);
        Task print = app.PrintAboveAsync (owner, "completed message", TestContext.Current.CancellationToken);
        app.LayoutAndDraw ();

        Assert.False (mouseReceived);
        Assert.Equal ("a", input.Text);
        Assert.True (print.IsCompletedSuccessfully);
        Assert.Contains ("completed message", bytes.ToString ());
        Assert.DoesNotContain (EscSeqUtils.CSI_EnableMouseEvents, bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void Disable_ReleasesGrabEvenIfNormalUngrabIsCanceledAndStopsRepetition ()
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using View view = new () { MouseState = MouseState.Pressed };
        Mock<IMouseHoldRepeater> repeater = new ();
        view.MouseHoldRepeater = repeater.Object;
        app.Mouse.GrabMouse (view);
        app.Mouse.UnGrabbingMouse += (_, e) => e.Cancel = true;
        int released = 0;
        app.Mouse.UnGrabbedMouse += (_, _) => released++;

        app.Mouse.IsMouseDisabled = true;

        Assert.False (app.Mouse.IsGrabbed ());
        Assert.Equal (MouseState.None, view.MouseState);
        Assert.Equal (1, released);
        repeater.Verify (instance => instance.Stop (), Times.Once);
    }

    [Fact]
    public void Disable_ClearsHoverAndKeepsEventSubscriptions ()
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using View view = new ();
        app.Mouse.RaiseMouseEnterLeaveEvents (Point.Empty, [view]);
        Assert.Contains (view, app.Mouse.CachedViewsUnderMouse);
        bool mouseReceived = false;
        app.Mouse.MouseEvent += (_, _) => mouseReceived = true;

        app.Mouse.IsMouseDisabled = true;

        Assert.Empty (app.Mouse.CachedViewsUnderMouse);
        Assert.Equal (MouseState.None, view.MouseState);

        app.Mouse.IsMouseDisabled = false;
        app.InjectMouse (new Mouse { Flags = MouseFlags.PositionReport, ScreenPosition = Point.Empty });
        Assert.True (mouseReceived);
    }

    // CoPilot - GPT-6
    [Fact]
    public void DisableAndReenableDuringHold_DoesNotDuplicateTickCommands ()
    {
        VirtualTimeProvider time = new ();
        using ApplicationImpl app = Create (new StringBuilder (), timeProvider: time);
        using Runnable top = new () { Width = Dim.Fill (), Height = 10 };
        View view = new () { Width = 5, Height = 1, CanFocus = true, MouseHighlightStates = MouseState.Pressed, MouseHoldRepeat = MouseFlags.LeftButtonReleased };
        top.Add (view);
        SessionToken token = app.Begin (top)!;
        app.LayoutAndDraw ();
        TimeSpan interval = TimeSpan.FromMilliseconds (100);
        view.MouseHoldRepeater = new MouseHoldRepeaterImpl (view, app.TimedEvents, app.Mouse)
        {
            Timeout = new () { Span = interval, Callback = null! }
        };
        int activated = 0;
        view.Activating += (_, e) =>
        {
            activated++;
            e.Handled = true;
        };

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            app.Mouse.RaiseMouseEvent (new () { ScreenPosition = Point.Empty, Flags = MouseFlags.LeftButtonPressed });
            Assert.True (app.Mouse.IsGrabbed (view));
            time.Advance (interval);
            app.TimedEvents!.RunTimers ();
            Assert.Equal (cycle, activated);

            app.Mouse.IsMouseDisabled = true;
            Assert.False (app.Mouse.IsGrabbed ());
            Assert.Equal (MouseState.None, view.MouseState);
            Assert.Empty (app.TimedEvents.Timeouts);
            app.Mouse.RaiseMouseEvent (new () { ScreenPosition = Point.Empty, Flags = MouseFlags.LeftButtonReleased });
            time.Advance (interval);
            app.TimedEvents.RunTimers ();
            Assert.Equal (cycle, activated);
            app.Mouse.IsMouseDisabled = false;
        }

        app.End (token);
    }

    [Fact]
    public void RetainedMouse_AfterDispose_DoesNotWriteToTerminal ()
    {
        StringBuilder bytes = new ();
        ApplicationImpl app = Create (bytes);
        IMouse mouse = app.Mouse;
        app.Dispose ();
        bytes.Clear ();

        mouse.IsMouseDisabled = true;
        mouse.IsMouseDisabled = false;

        Assert.Empty (bytes.ToString ());
    }

    // Copilot - Claude Opus 5.5
    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void DisableFromPressHandler_StopsDispatchBeforeSuperViewCanGrab (bool superViewAutoGrabs)
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using Runnable top = new () { Width = Dim.Fill (), Height = 10 };
        View superView = new () { Width = 20, Height = 5, MouseHighlightStates = superViewAutoGrabs ? MouseState.Pressed : MouseState.None };
        View subView = new () { Width = 5, Height = 1, CanFocus = true, MouseHighlightStates = MouseState.Pressed };
        subView.MouseBindings.ReplaceCommands (MouseFlags.LeftButtonPressed, Command.Activate);
        subView.Activating += (_, _) => app.Mouse.IsMouseDisabled = true;
        superView.Add (subView);
        top.Add (superView);
        SessionToken token = app.Begin (top)!;
        app.LayoutAndDraw ();

        app.Mouse.RaiseMouseEvent (new () { ScreenPosition = new (1, 0), Flags = MouseFlags.LeftButtonPressed });

        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.False (app.Mouse.IsGrabbed ());
        Assert.Equal (MouseState.None, superView.MouseState);
        Assert.Equal (MouseState.None, subView.MouseState);
        app.End (token);
    }

    // Copilot - Claude Opus 5.5
    [Fact]
    public void DisableFromEnterHandler_DoesNotRepopulateHover ()
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using View first = new () { Frame = new (0, 0, 5, 1) };
        using View second = new () { Frame = new (0, 0, 5, 1) };
        int left = 0;
        first.MouseEnter += (_, _) => app.Mouse.IsMouseDisabled = true;
        first.MouseLeave += (_, _) => left++;

        app.Mouse.RaiseMouseEnterLeaveEvents (Point.Empty, [first, second]);

        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.Empty (app.Mouse.CachedViewsUnderMouse);
        Assert.Equal (MouseState.None, first.MouseState);
        Assert.Equal (MouseState.None, second.MouseState);
        Assert.Equal (1, left);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (false, false, false)]
    [InlineData (true, false, false)]
    [InlineData (false, true, false)]
    [InlineData (true, true, false)]
    [InlineData (false, true, true)]
    [InlineData (true, true, true)]
    public void DisableFromGrabCallback_DoesNotInstallGrabOrContinuePress (bool afterGrab, bool autoGrab, bool repeat)
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using Runnable top = new () { Width = Dim.Fill (), Height = 10 };
        View view = new () { Width = 5, Height = 1, CanFocus = true, MouseHighlightStates = MouseState.Pressed };
        view.MouseHoldRepeat = repeat ? MouseFlags.LeftButtonPressed : null;
        top.Add (view);
        SessionToken token = app.Begin (top)!;
        app.LayoutAndDraw ();
        int timeoutsBefore = app.TimedEvents!.Timeouts.Count;
        int activated = 0;
        view.MouseBindings.ReplaceCommands (MouseFlags.LeftButtonPressed, Command.Activate);
        view.Activating += (_, _) => activated++;
        if (afterGrab)
        {
            app.Mouse.GrabbedMouse += (_, _) => app.Mouse.IsMouseDisabled = true;
        }
        else
        {
            app.Mouse.GrabbingMouse += (_, _) => app.Mouse.IsMouseDisabled = true;
        }

        if (autoGrab)
        {
            app.Mouse.RaiseMouseEvent (new () { ScreenPosition = new (1, 0), Flags = MouseFlags.LeftButtonPressed });
        }
        else
        {
            app.Mouse.GrabMouse (view);
        }

        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.False (app.Mouse.IsGrabbed ());
        Assert.Equal (MouseState.None, view.MouseState);
        Assert.Equal (0, activated);
        Assert.Equal (timeoutsBefore, app.TimedEvents.Timeouts.Count);
        app.End (token);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void DisableFromLowLevelMouseEvent_StopsPressStateAndCommands (bool autoGrab)
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using Runnable top = new () { Width = Dim.Fill (), Height = 10 };
        View view = new () { Width = 5, Height = 1, CanFocus = true, MouseHighlightStates = autoGrab ? MouseState.Pressed : MouseState.None };
        top.Add (view);
        SessionToken token = app.Begin (top)!;
        app.LayoutAndDraw ();
        int activated = 0;
        view.MouseBindings.ReplaceCommands (MouseFlags.LeftButtonPressed, Command.Activate);
        view.Activating += (_, _) => activated++;
        view.MouseEvent += (_, _) => app.Mouse.IsMouseDisabled = true;

        app.Mouse.RaiseMouseEvent (new () { ScreenPosition = new (1, 0), Flags = MouseFlags.LeftButtonPressed });

        Assert.True (app.Mouse.IsMouseDisabled);
        Assert.False (app.Mouse.IsGrabbed ());
        Assert.Equal (MouseState.None, view.MouseState);
        Assert.Equal (0, activated);
        app.End (token);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (AppModel.Inline)]
    [InlineData (AppModel.FullScreen)]
    public void ConcurrentToggle_KeepsTerminalTransitionAndPublicationUnderRenderGate (AppModel model)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using ApplicationImpl app = Create (new StringBuilder (), model: model);
        AnsiOutput output = Assert.IsType<AnsiOutput> (Assert.IsType<DriverImpl> (app.Driver).GetOutput ());
        IMainLoopCoordinator original = app.Coordinator!;
        IInlineRenderCoordinator renderGate = Assert.IsAssignableFrom<IInlineRenderCoordinator> (original);
        IMouseReportingCoordinator reporting = Assert.IsAssignableFrom<IMouseReportingCoordinator> (original);
        using ManualResetEventSlim transitionFinished = new ();
        using ManualResetEventSlim releasePublication = new ();
        using ManualResetEventSlim uiStarted = new ();
        using ManualResetEventSlim uiFinished = new ();
        int workerId = 0;
        Mock<IMainLoopCoordinator> proxy = new ();
        proxy.As<IInlineRenderCoordinator> ().Setup (instance => instance.WithRenderGate (It.IsAny<Action> ()))
             .Callback<Action> (renderGate.WithRenderGate);
        proxy.As<IMouseReportingCoordinator> ().Setup (instance => instance.SetMouseDisabled (It.IsAny<bool> ()))
             .Callback<bool> (disabled =>
             {
                 reporting.SetMouseDisabled (disabled);
                 if (Environment.CurrentManagedThreadId != workerId)
                 {
                     return;
                 }
                 transitionFinished.Set ();
                 Assert.True (releasePublication.Wait (TimeSpan.FromSeconds (5), cancellationToken));
             });
        Exception? workerFailure = null;
        Exception? uiFailure = null;
        Thread worker = new (() =>
        {
            try
            {
                workerId = Environment.CurrentManagedThreadId;
                app.Mouse.IsMouseDisabled = true;
            }
            catch (Exception error) { workerFailure = error; }
        })
        { IsBackground = true };
        Thread ui = new (() =>
        {
            try
            {
                uiStarted.Set ();
                app.Mouse.IsMouseDisabled = true;
                app.Mouse.IsMouseDisabled = false;
            }
            catch (Exception error) { uiFailure = error; }
            finally { uiFinished.Set (); }
        })
        { IsBackground = true };
        bool finishedBeforePublication = false;
        SetCoordinator (proxy.Object);
        try
        {
            worker.Start ();
            Assert.True (transitionFinished.Wait (TimeSpan.FromSeconds (5), cancellationToken));
            ui.Start ();
            Assert.True (uiStarted.Wait (TimeSpan.FromSeconds (5), cancellationToken));
            finishedBeforePublication = uiFinished.Wait (TimeSpan.FromMilliseconds (100), cancellationToken);
        }
        finally
        {
            releasePublication.Set ();
            try
            {
                Assert.True (worker.Join (TimeSpan.FromSeconds (5)));
                if ((ui.ThreadState & ThreadState.Unstarted) == 0)
                {
                    Assert.True (ui.Join (TimeSpan.FromSeconds (5)));
                }
            }
            finally
            {
                SetCoordinator (original);
            }
        }

        Assert.Null (workerFailure);
        Assert.Null (uiFailure);
        Assert.False (finishedBeforePublication);
        Assert.Equal (output.IsMouseDisabled, app.Mouse.IsMouseDisabled);
        Assert.False (app.Mouse.IsMouseDisabled);

        // Decorate the real coordinator to pause exactly between its physical transition and the setter's publication.
        void SetCoordinator (IMainLoopCoordinator coordinator) => typeof (ApplicationImpl)
            .GetProperty (nameof (ApplicationImpl.Coordinator), BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue (app, coordinator);
    }

    // Copilot - Claude Opus 5.5
    [Fact]
    public void GrabMouse_WhileDisabled_IsIgnored ()
    {
        using ApplicationImpl app = Create (new StringBuilder (), true);
        using View view = new ();
        bool grabbing = false;
        app.Mouse.GrabbingMouse += (_, _) => grabbing = true;

        app.Mouse.GrabMouse (view);

        Assert.False (app.Mouse.IsGrabbed ());
        Assert.False (grabbing);
    }

    // Copilot - Claude Opus 5.5
    [Fact]
    public void DisableFromOtherThread_WritesTerminalNowAndQueuesViewRelease ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes);
        using View view = new () { MouseState = MouseState.Pressed };
        app.Mouse.GrabMouse (view);
        bytes.Clear ();
        int? mainThreadId = app.MainThreadId;
        app.MainThreadId = Environment.CurrentManagedThreadId + 1;

        try
        {
            app.Mouse.IsMouseDisabled = true;
        }
        finally
        {
            app.MainThreadId = mainThreadId;
        }

        Assert.Equal (EscSeqUtils.CSI_DisableMouseEvents, bytes.ToString ());
        Assert.True (app.Mouse.IsGrabbed (view));

        app.TimedEvents!.RunTimers ();

        Assert.False (app.Mouse.IsGrabbed ());
        Assert.Equal (MouseState.None, view.MouseState);
    }

    // Copilot - Claude Opus 5.5
    [Fact]
    public void QueuedViewRelease_IsSkippedIfReenabledFirst ()
    {
        using ApplicationImpl app = Create (new StringBuilder ());
        using View view = new () { MouseState = MouseState.Pressed };
        app.Mouse.GrabMouse (view);
        int? mainThreadId = app.MainThreadId;
        app.MainThreadId = Environment.CurrentManagedThreadId + 1;

        try
        {
            app.Mouse.IsMouseDisabled = true;
        }
        finally
        {
            app.MainThreadId = mainThreadId;
        }

        app.Mouse.IsMouseDisabled = false;
        app.TimedEvents!.RunTimers ();

        Assert.True (app.Mouse.IsGrabbed (view));
        Assert.Equal (MouseState.Pressed, view.MouseState);
    }
}
