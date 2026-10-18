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
        ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: sizeMonitor), time ?? new VirtualTimeProvider ())
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 15)
        };
        app.Init ();
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

        app.Driver!.SetScreenSize (50, 20);
        app.LayoutAndDraw (true);
        output.SetCursor (new Cursor { Position = Point.Empty });

        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        Assert.False (pending.IsCompleted);
        Assert.True (output.IsInlineResizePending);

        Reply (app, "\u001b[11;6R");
        app.LayoutAndDraw ();

        Assert.True (pending.IsCompletedSuccessfully);
        Assert.Equal (new Rectangle (0, 10, 50, 3), app.Screen);
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
    [InlineData ("\u001b[20;1R", 17)]
    public void Resize_UsesReportedCursorAndPreviousRelativeRow (string response, int expectedOrigin)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        bytes.Clear ();
        app.Driver!.SetScreenSize (50, 20);

        Reply (app, response);
        app.LayoutAndDraw ();

        Assert.Equal (expectedOrigin, app.Screen.Y);
        Assert.Equal (50, app.Screen.Width);
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
        app.Driver.SetScreenSize (50, 20);
        app.LayoutAndDraw ();
        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();
        Assert.True (pending.IsCompletedSuccessfully);
        app.End (owner);
    }

    [Theory]
    [InlineData (null)]
    [InlineData ("\u001b[0;1R")]
    [InlineData ("\u001b[21;1R")]
    [InlineData ("\u001b[10;51R")]
    public void MissingOrInvalidPosition_FaultsPrintingWithoutErasingTranscript (string? response)
    {
        VirtualTimeProvider time = new ();
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out AnsiOutput output, time);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (50, 20);
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        bytes.Clear ();
        if (response is { })
        {
            Reply (app, response);
        }
        time.Advance (TimeSpan.FromSeconds (3));

        app.LayoutAndDraw ();
        output.SetCursor (new Cursor ());

        Assert.True (pending.IsFaulted);
        Assert.IsType<IOException> (pending.Exception!.InnerException);
        Assert.False (output.IsInlinePlacementKnown);
        Assert.Empty (bytes.ToString ());
        app.End (owner);
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void SecondResize_DiscardsResponseForPreviousSizeWithoutErasing (bool returnToFirstSize)
    {
        StringBuilder bytes = new ();
        using ApplicationImpl app = Create (bytes, out _);
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        app.Driver!.SetScreenSize (50, 20);
        app.Driver.SetScreenSize (40, 18);
        if (returnToFirstSize)
        {
            app.Driver.SetScreenSize (50, 20);
        }
        Task pending = app.PrintAboveAsync (owner, "completed", TestContext.Current.CancellationToken);
        bytes.Clear ();

        Reply (app, "\u001b[10;1R");
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 15, 90, 3), app.Screen);
        Assert.False (pending.IsCompleted);
        Assert.DoesNotContain ("[2K", bytes.ToString ());
        app.End (owner);
        Assert.True (pending.IsCanceled);
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
        app.Driver!.SetScreenSize (50, 20);
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
        app.Driver!.SetScreenSize (50, 20);
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
        app.Driver!.SetScreenSize (50, 20);
        Reply (app, "\u001b[10;1R");
        EventHandler<EventArgs<Rectangle>> throwing = (_, _) => throw new InvalidOperationException ("subscriber");
        app.ScreenChanged += throwing;

        Assert.Throws<InvalidOperationException> (() => app.LayoutAndDraw ());

        app.ScreenChanged -= throwing;
        Assert.Equal (50, app.Screen.Width);
        Assert.False (output.IsInlineResizePending);
        bytes.Clear ();
        app.LayoutAndDraw ();
        Assert.Contains ("ready", bytes.ToString ());
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
