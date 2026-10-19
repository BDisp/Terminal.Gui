using System.Text;
using Moq;

namespace ApplicationTests;

// CoPilot - GPT-6
[Collection ("Application Tests")]
public class InlineResizeTests
{
    private static ApplicationImpl Create (StringBuilder bytes, out AnsiOutput output, VirtualTimeProvider? time = null, Func<bool>? accept = null)
    {
        output = new AnsiOutput (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return accept?.Invoke () ?? true; }, AppModel.Inline);
        output.SetSize (90, 25);
        ISizeMonitor sizeMonitor = Mock.Of<ISizeMonitor> (monitor => monitor.InitialSizeReceived == true);
        VirtualTimeProvider clock = time ?? new ();
        ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: sizeMonitor), clock)
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 15)
        };
        app.Init ();
        ((DriverImpl)app.Driver!).AnsiRequestScheduler.Now = () => clock.Now;
        return app;
    }

    private static void Reply (ApplicationImpl app, string response) =>
        ((AnsiResponseParser<char>)((DriverImpl)app.Driver!).GetInputProcessor ().GetParser ())
        .ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());

    [Fact]
    public void Resize_WaitsForCursorWithoutClearingOrPrintingAtStaleCoordinates ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (5, 1), Style = CursorStyle.SteadyBar });
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        app.Driver!.SetScreenSize (90, 20);
        app.LayoutAndDraw (true);
        app.Driver.Refresh ();
        output.SetCursor (new Cursor { Position = Point.Empty });

        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        Assert.False (pending.IsCompleted);
        Assert.True (output.IsInlineResizePending);

        Reply (app, "\u001b[11;6R");
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Equal (new Rectangle (0, 10, 90, 3), app.Screen);
        Assert.Equal (new Point (0, 10), app.Driver.InlinePosition);
        Assert.False (output.IsInlineResizePending);
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        Assert.Contains ("completed", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData ("\u001b[2;1R", 0)]
    [InlineData ("\u001b[?5;1R", 3)]
    [InlineData ("\u001b[19;1R", 17)]
    public void Resize_UsesReportedCursorAndPreviousRelativeRow (string response, int expectedOrigin)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        bytes.Clear ();
        app.Driver!.SetScreenSize (90, 20);

        Reply (app, response);
        app.LayoutAndDraw ();

        Assert.Equal (expectedOrigin, app.Screen.Y);
        Assert.Equal (90, app.Screen.Width);
        Assert.DoesNotContain ("\n", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void Resize_ToZero_DefersWithoutErasingOrFaultingUntilSizeReturns ()
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        app.Driver!.SetScreenSize (0, 0);
        time.Advance (TimeSpan.FromSeconds (10));
        app.LayoutAndDraw ();

        Assert.Empty (bytes.ToString ());
        Assert.False (pending.IsCompleted);
        Assert.True (output.IsInlinePlacementKnown);
        app.Driver.SetScreenSize (90, 20);
        app.LayoutAndDraw ();
        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();
        Assert.True (pending.IsCompletedSuccessfully);
        app.End (owner);
    }

    [Theory]
    [InlineData (null)]
    [InlineData ("\u001b[0;1R")]
    [InlineData ("\u001b[1;1R")]
    [InlineData ("\u001b[1;2R")]
    [InlineData ("\u001b[20;1R")]
    [InlineData ("\u001b[21;1R")]
    [InlineData ("\u001b[10;91R")]
    public void MissingOrInvalidPosition_ReservesFreshRegionAndCompletesPrinting (string? response)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();
        if (response is { })
        {
            Reply (app, response);
        }
        time.Advance (TimeSpan.FromSeconds (3));

        app.LayoutAndDraw ();
        output.SetCursor (new Cursor ());

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void SecondResize_UsesFreshRegionRatherThanTrustingSupersededReport (bool returnToFirstSize)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out _);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        app.Driver.SetScreenSize (90, 18);
        if (returnToFirstSize)
        {
            app.Driver.SetScreenSize (90, 20);
        }
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 1, 90, 3), app.Screen);
        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Contains ("\n", bytes.ToString ());
        app.End (owner);
        Assert.True (pending.IsCompletedSuccessfully);
    }

    [Fact]
    public void FailedResizeErase_PreservesPublishedGeometryAndFaultsPrinting ()
    {
        bool accept = true;
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, accept: () => accept);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        accept = false;

        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        Assert.False (output.IsInlinePlacementKnown);
        Assert.True (pending.IsFaulted);
        accept = true;
        bytes.Clear ();
        app.LayoutAndDraw (true);
        output.SetCursor (new Cursor ());
        Assert.Empty (bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void ResizeNotification_CanEndSessionOrDisposeWithoutPrinting (bool dispose)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out _);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        app.ScreenChanged += Stop;
        bytes.Clear ();

        app.LayoutAndDraw ();

        Assert.True (pending.IsCanceled);
        Assert.DoesNotContain ("must not print", bytes.ToString ());

        void Stop (object? _, EventArgs<Rectangle> __)
        {
            app.ScreenChanged -= Stop;
            if (dispose)
            {
                app.Dispose ();
                return;
            }
            app.End (owner);
        }
    }

    [Fact]
    public void ThrowingResizeNotification_LeavesNewGeometryReadyForRepaint ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "ready" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        EventHandler<EventArgs<Rectangle>> throwing = (_, _) => throw new InvalidOperationException ("subscriber");
        app.ScreenChanged += throwing;

        Assert.Throws<InvalidOperationException> (() => app.LayoutAndDraw ());

        app.ScreenChanged -= throwing;
        Assert.Equal (90, app.Screen.Width);
        Assert.False (output.IsInlineResizePending);
        bytes.Clear ();
        app.LayoutAndDraw ();
        Assert.Contains ("ready", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void RedrawBeforeResize_UsesPhysicalFrameRowInsteadOfHiddenCaretCache ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        app.LayoutAndDraw (true);
        Assert.Null (output.GetCursor ().Position);
        Assert.Equal (15, output.InlineCursorRow);

        app.Driver!.SetScreenSize (90, 26);
        Reply (app, "\u001b[16;1R");
        app.LayoutAndDraw ();

        Assert.Equal (15, app.Screen.Y);
        app.End (owner);
    }

    [Fact]
    public void SecondResizeAfterThrowingNotification_UsesRecoveredPhysicalOrigin ()
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        EventHandler<EventArgs<Rectangle>> throwing = (_, _) => throw new InvalidOperationException ("subscriber");
        app.ScreenChanged += throwing;
        Assert.Throws<InvalidOperationException> (() => app.LayoutAndDraw ());
        app.ScreenChanged -= throwing;
        Assert.Equal (8, app.Screen.Y);
        Assert.Equal (8, output.InlineCursorRow);
        Assert.Equal (1, output.GetCursor ().Position!.Value.Y);
        time.Advance (TimeSpan.FromMilliseconds (200));
        bytes.Clear ();

        app.Driver.SetScreenSize (90, 18);
        Reply (app, "\u001b[9;1R");
        app.LayoutAndDraw ();

        Assert.Equal (8, app.Screen.Y);
        Assert.DoesNotContain ("[8;1H", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    [InlineData (2)]
    public void DelayedReplyAfterAbandonmentOrTimeout_UsesFreshRegionWithoutReplacementQuery (int failure)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        app.Driver.SetScreenSize (90, 18);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();
        if (failure == 0)
        {
            time.Advance (TimeSpan.FromSeconds (3));
        }
        else if (failure == 1)
        {
            ((DriverImpl)app.Driver).GetInputProcessor ().GetParser ().StopExpecting ("R", null, false);
        }
        else
        {
            time.Advance (TimeSpan.FromMilliseconds (1100));
            ((DriverImpl)app.Driver).AnsiRequestScheduler.RunSchedule (app.Driver, force: true);
            Assert.True (((DriverImpl)app.Driver).AnsiRequestScheduler.HasAbandonedCursorPositionRequest);
        }
        // A further resize must not clear the failure before it has been processed.
        app.Driver.SetScreenSize (90, 22);
        app.LayoutAndDraw ();
        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Equal (1, app.Screen.Y);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void ExistingCursorRequest_IsConsumedBeforeResizeRequestIsSent ()
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        bool received = false;
        app.Driver!.QueueAnsiRequest (new AnsiEscapeSequenceRequest
        {
            Request = "\u001b[6n", Terminator = "R", ResponseReceived = _ => received = true
        });
        bytes.Clear ();
        app.Driver.SetScreenSize (90, 20);
        app.LayoutAndDraw ();
        Assert.Empty (bytes.ToString ());
        Reply (app, "\u001b[16;1R");
        Assert.True (received);
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Reply (app, "\u001b[16;1R");
        app.LayoutAndDraw ();
        Assert.Equal (15, app.Screen.Y);
        Assert.Equal (90, app.Screen.Width);
        Assert.True (output.IsInlinePlacementKnown);
        app.End (owner);
    }

    [Fact]
    public void InvalidReplyAfterRevisionChange_UsesFreshRegion ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        app.Driver.SetScreenSize (90, 18);
        Reply (app, "\u001b[0;1R");
        app.Driver.SetScreenSize (90, 22);
        bytes.Clear ();
        app.LayoutAndDraw ();
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void AbandonedEarlierCursorRequest_UsesFreshRegionWithoutAcceptingLateReply ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.QueueAnsiRequest (new AnsiEscapeSequenceRequest { Request = "\u001b[6n", Terminator = "R", ResponseReceived = _ => { } });
        ((DriverImpl)app.Driver).GetInputProcessor ().GetParser ().StopExpecting ("R", null, false);
        bytes.Clear ();

        app.Driver.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();

        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (0, 20)]
    [InlineData (50, 0)]
    [InlineData (0, 0)]
    public void HeadlessZeroSize_RetainsOriginUntilPositiveDimensionsReturn (int width, int height)
    {
        using IApplication app = Application.Create ();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point (0, 15);
        app.Init (DriverRegistry.Names.ANSI);
        app.Screen = new Rectangle (0, 15, 90, 3);
        app.Driver!.SetScreenSize (width, height);
        app.LayoutAndDraw ();
        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);

        app.Driver.SetScreenSize (50, 20);
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 15, 50, 3), app.Screen);
        Assert.Equal (new Point (0, 15), app.Driver.InlinePosition);
    }

    [Fact]
    public void NativeResizeBetweenPollAndCaretUpdate_SuppressesStaleWrites ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        int? physical = output.InlineCursorRow;
        Size nativeSize = new (90, 25);
        output.NativeSizeQuery = () => nativeSize;
        nativeSize = new Size (90, 20);
        bytes.Clear ();

        output.SetCursor (new Cursor { Position = Point.Empty, Style = CursorStyle.SteadyBar });
        app.Driver!.Refresh ();

        Assert.Empty (bytes.ToString ());
        Assert.Equal (physical, output.InlineCursorRow);
        app.Driver.SetScreenSize (90, 20);
        Reply (app, "\u001b[16;1R");
        app.LayoutAndDraw ();
        Assert.Equal (15, app.Screen.Y);
        Assert.Equal (90, app.Screen.Width);
        Assert.True (output.IsInlinePlacementKnown);
        app.End (owner);
    }

    [Theory]
    [InlineData (true)]
    [InlineData (false)]
    public void SupersededReplyBeyondOldBounds_DefersUntilPollingThenUsesFreshRegion (bool polled)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        output.NativeSizeQuery = () => new Size (90, 30);
        if (polled)
        {
            app.Driver.SetScreenSize (90, 30);
        }
        bytes.Clear ();
        Reply (app, "\u001b[25;80R");
        app.LayoutAndDraw ();
        Assert.True (output.IsInlinePlacementKnown);
        if (!polled)
        {
            Assert.False (pending.IsCompleted);
            Assert.Empty (bytes.ToString ());
            app.Driver.SetScreenSize (90, 30);
        }
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        Reply (app, "\u001b[18;1R");
        app.LayoutAndDraw ();
        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Equal (90, app.Screen.Width);
        Assert.Equal (1, app.Screen.Y);
        Assert.DoesNotContain ("\u001b[6n", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void NativeResizeBeforeDisposal_RestoresModesWithoutStaleCursorPlacement ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        app.Begin (composer);
        app.LayoutAndDraw ();
        output.NativeSizeQuery = () => new Size (90, 10);
        bytes.Clear ();

        app.Dispose ();

        Assert.Contains ("\u001b[?2004l", bytes.ToString ());
        Assert.Contains ("\u001b[?25h", bytes.ToString ());
        Assert.Contains ("\u001b[10;1H\n", bytes.ToString ());
        Assert.DoesNotContain ("\u001b[18;1H", bytes.ToString ());
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void NativeResizeBeforeRecoveryErase_DefersUntilNextSizePoll (bool changesDuringRecovery)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        int queries = 0;
        output.NativeSizeQuery = () => changesDuringRecovery && ++queries == 1 ? new Size (90, 20) : new Size (90, 18);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();
        app.LayoutAndDraw ();
        Assert.Empty (bytes.ToString ());
        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        Assert.True (output.IsInlineResizePending);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.False (pending.IsCompleted);

        app.Driver.SetScreenSize (90, 18);
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        Reply (app, "\u001b[9;1R");
        app.LayoutAndDraw ();
        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Equal (90, app.Screen.Width);
        app.End (owner);
    }

    [Theory]
    [InlineData (1)]
    [InlineData (6)]
    [InlineData (3)]
    public void NativeResizeBeforeShrinkGrowOrPrint_DefersRawWrites (int requestedHeight)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        composer.Height = requestedHeight;
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        output.NativeSizeQuery = () => new Size (90, 20);
        bytes.Clear ();

        app.LayoutAndDraw ();

        Assert.Empty (bytes.ToString ());
        Assert.False (pending.IsCompleted);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        app.Driver!.SetScreenSize (90, 20);
        app.LayoutAndDraw ();
        Reply (app, "\u001b[18;1R");
        app.LayoutAndDraw ();
        Assert.True (pending.IsCompletedSuccessfully);
        app.End (owner);
    }

    [Fact]
    public void NativeClampObservedImmediatelyBeforeErase_UsesFreshRegionAfterReturningToPolledSize ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        int queries = 0;
        output.NativeSizeQuery = () => ++queries == 1 ? new Size (90, 20) : new Size (90, 3);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();
        app.LayoutAndDraw ();
        Assert.Empty (bytes.ToString ());
        Assert.False (pending.IsCompleted);
        Assert.True (output.IsInlineResizePending);

        // No size event is delivered: the terminal returns to the dimensions already polled.
        output.NativeSizeQuery = () => new Size (90, 20);
        app.LayoutAndDraw ();
        Assert.Empty (bytes.ToString ());
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Equal (1, app.Screen.Y);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    [InlineData (2)]
    public void NativeSizesReturningBeforePolling_RequireRecoveryBeforeWritesResume (int surface)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Size native = new (90, 23);
        output.NativeSizeQuery = () => native;
        bytes.Clear ();
        Observe ();
        native = new Size (90, 25);
        Observe ();
        output.SetCursor (new Cursor { Position = Point.Empty });
        Assert.Empty (bytes.ToString ());

        app.LayoutAndDraw ();
        Assert.Contains ("\n", bytes.ToString ());

        Assert.Equal (new Rectangle (0, 0, 90, 3), app.Screen);
        Assert.False (output.IsInlineResizePending);
        Assert.True (output.IsInlinePlacementKnown);
        app.End (owner);

        void Observe ()
        {
            if (surface == 0)
            {
                output.Write (((DriverImpl)app.Driver!).GetOutputBuffer ());
                return;
            }
            if (surface == 1)
            {
                output.SetCursor (new Cursor { Position = Point.Empty });
                return;
            }
            output.GetSize ();
        }
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void ExcessiveUnprocessedNativeTransitions_ReserveFreshRegionAndResumeWrites (bool overflowReturnsToPolledSize)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Size native = new (90, 25);
        output.NativeSizeQuery = () => native;
        bytes.Clear ();
        for (var i = 0; i < 100; i++)
        {
            int width = i % 2 == 0 ? 50 : 90;
            if (overflowReturnsToPolledSize)
            {
                width = i < 64 ? 50 + i % 2 * 10 : 90;
            }

            native = new Size (width, 25);
            output.SetCursor (new Cursor { Position = Point.Empty });
        }
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (0)]
    [InlineData (18)]
    public void NativeSizeProviderBecomesUnavailable_KeepsAcceptedReportInvalidUntilPollingCatchesUp (int observedHeight)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[10;1R");
        output.NativeSizeQuery = () => new Size (90, observedHeight);
        bytes.Clear ();
        app.LayoutAndDraw ();
        output.NativeSizeQuery = () => null;
        app.LayoutAndDraw ();

        Assert.Empty (bytes.ToString ());
        Assert.True (output.IsInlineResizePending);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Equal (15, app.Screen.Y);

        app.Driver.SetScreenSize (90, observedHeight == 0 ? 20 : observedHeight);
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        Assert.Contains ("\n", bytes.ToString ());
        Assert.Equal (0, app.Screen.Y);
        Assert.False (output.IsInlineResizePending);
        app.End (owner);
    }

    [Fact]
    public void PartialWidthResize_DoesNotEraseUnownedColumns ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Screen = new Rectangle (0, 15, 40, 3);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        app.Driver!.SetScreenSize (90, 20);
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void FailedFrameBeforeResize_DoesNotTrustCachedCaretOrPartiallyWrittenPosition ()
    {
        bool accept = true;
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, accept: () => accept);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        accept = false;
        app.LayoutAndDraw (true);
        accept = true;
        Assert.Null (output.InlineCursorRow);
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        app.Driver!.SetScreenSize (90, 20);
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.Contains ("\n", bytes.ToString ());
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void ResizeInDrawingCallback_StopsStaleFrameBeforeDriverRefresh ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        composer.DrawingContent += Resize;
        bytes.Clear ();

        app.LayoutAndDraw (true);

        Assert.True (output.IsInlineResizePending);
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        app.End (owner);

        void Resize (object? _, DrawEventArgs __)
        {
            composer.DrawingContent -= Resize;
            app.Driver!.SetScreenSize (90, 20);
        }
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void PendingRasterDuringResize_UsesFreshRegionInsteadOfErasingOldCells (bool queuedAfterResize)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        DriverImpl driver = (DriverImpl)app.Driver!;
        if (queuedAfterResize)
        {
            driver.SetScreenSize (90, 20);
            Reply (app, "\u001b[15;1R");
        }

        driver.GetOutputBuffer ().AddRasterImage (new RasterImageCommand
        {
            Id = "image", DestinationCells = new Rectangle (0, 0, 1, 1), Pixels = new Color [1, 1]
        });
        bytes.Clear ();
        if (!queuedAfterResize)
        {
            driver.SetScreenSize (90, 20);
        }

        app.LayoutAndDraw ();

        string writes = bytes.ToString ();
        Assert.Contains ("\u001b[20;1H" + new string ('\n', 20), writes);
        Assert.DoesNotContain ("[2K", writes [..writes.IndexOf ('\n')]);
        Assert.Equal (0, app.Screen.Y);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.False (output.IsInlineResizePending);
        app.End (owner);
    }

    [Fact]
    public void ThrottledCursorQuery_RetriesWithoutQueuingDuplicateRequests ()
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.QueueAnsiRequest (new AnsiEscapeSequenceRequest { Request = "\u001b[6n", Terminator = "R", ResponseReceived = _ => { } });
        Reply (app, "\u001b[16;1R");
        bytes.Clear ();

        app.Driver.SetScreenSize (90, 20);
        app.LayoutAndDraw ();
        Assert.Empty (bytes.ToString ());
        Assert.True (output.IsInlineResizePending);
        Assert.DoesNotContain (((DriverImpl)app.Driver).AnsiRequestScheduler.QueuedRequests, request => request.Terminator == "R");
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Reply (app, "\u001b[15;1R");
        app.LayoutAndDraw ();

        Assert.False (output.IsInlineResizePending);
        Assert.Equal (14, app.Screen.Y);
        app.End (owner);
    }

    [Fact]
    public void NativeTransitionsReturningDuringFinalGuard_DeferPlannedEraseAndUseFreshRegion ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[15;1R");
        int reads = 0;
        bool nested = false;
        Size native = new (90, 20);
        output.NativeSizeQuery = () =>
        {
            if (nested || ++reads != 2)
            {
                return native;
            }

            nested = true;
            native = new Size (50, 20);
            output.GetSize ();
            native = new Size (90, 20);
            output.GetSize ();
            nested = false;
            return native;
        };
        bytes.Clear ();

        app.LayoutAndDraw ();

        Assert.Empty (bytes.ToString ());
        Assert.Equal (15, app.Screen.Y);
        Assert.True (output.IsInlineResizePending);
        app.LayoutAndDraw ();
        Assert.Contains ("\u001b[20;1H" + new string ('\n', 20), bytes.ToString ());
        Assert.Equal (0, app.Screen.Y);
        Assert.False (output.IsInlineResizePending);
        app.End (owner);
    }

    [Theory]
    [InlineData (0, 3)]
    [InlineData (22, 3)]
    [InlineData (24, 1)]
    [InlineData (0, 25)]
    public void WidthReflow_ReservesFreshRegionWithoutCursorQueryEvenAtEdges (int origin, int height)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        app.ForceInlinePosition = new Point (0, origin);
        using Runnable composer = new () { Width = Dim.Fill (), Height = height, Text = "live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        app.Driver!.SetScreenSize (50, 25);
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Equal (50, app.Screen.Width);
        Assert.Equal (height, app.Screen.Height);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.False (output.IsInlineResizePending);
        Assert.Contains ("\u001b[25;1H" + new string ('\n', 25) + "\u001b[1;1H", bytes.ToString ());
        Assert.DoesNotContain ("\u001b[6n", bytes.ToString ());
        Assert.DoesNotContain ("[2K", bytes.ToString () [..bytes.ToString ().IndexOf ('\n')]);
        app.End (owner);
    }

    [Fact]
    public void HeightShrink_ClampedCaretReservesFreshRegionWithoutPendingPrint ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        app.ForceInlinePosition = new Point (0, 22);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "still live" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 2), Style = CursorStyle.SteadyBar });
        bytes.Clear ();

        app.Driver!.SetScreenSize (90, 24);
        Reply (app, "\u001b[24;1R");
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 0, 90, 3), app.Screen);
        Assert.Contains ("still live", bytes.ToString ());
        Assert.True (output.IsInlinePlacementKnown);
        Assert.False (output.IsInlineResizePending);
        app.End (owner);
    }

    [Fact]
    public void Resize_RecoversNegativeOriginAndRepaintsVisiblePopover ()
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        using Popover<TextField, string> popover = new () { Width = 10, Height = 2 };
        app.Popovers!.Register (popover);
        app.Popovers.Show (popover);
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 5), Style = CursorStyle.SteadyBar });
        int draws = 0;
        popover.DrawingContent += (_, _) => draws++;
        bytes.Clear ();

        app.Driver!.SetScreenSize (90, 20);
        Reply (app, "\u001b[3;1R");
        app.LayoutAndDraw ();

        Assert.Equal (0, app.Screen.Y);
        Assert.Equal (6, app.Screen.Height);
        Assert.True (draws > 0);
        Assert.True (output.IsInlinePlacementKnown);
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        app.Popovers.Hide (popover);
        app.Popovers.DeRegister (popover);
        app.End (owner);
    }

    [Fact]
    public void HeadlessResize_RetainsOriginAndClampsRegionWithoutWholeDisplayErase ()
    {
        using IApplication app = Application.Create ();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point (0, 15);
        app.Init (DriverRegistry.Names.ANSI);
        app.Screen = new Rectangle (0, 15, 90, 3);
        app.Driver!.SetScreenSize (50, 20);

        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 15, 50, 3), app.Screen);
        Assert.Equal (new Point (0, 15), app.Driver.InlinePosition);
        Assert.False (app.ClearScreenNextIteration);
    }
}
