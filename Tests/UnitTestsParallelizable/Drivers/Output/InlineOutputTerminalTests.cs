using System.Text;
using Moq;
using System.Text.RegularExpressions;

namespace DriverTests.Output;

public class InlineOutputTerminalTests
{
    [Fact]
    public void ExhaustiveSmallTerminalsPreserveOrderAndNeverScrollLiveText ()
    {
        for (var height = 1; height <= 8; height++)
            for (var liveHeight = 1; liveHeight <= height; liveHeight++)
                for (var origin = 0; origin <= height - liveHeight; origin++)
                    for (var count = 1; count <= 3 * height + 1; count++)
                    {
                        Grid terminal = new (8, height);
                        for (var row = 0; row < origin; row++)
                        {
                            terminal.Rows [row] = $"S{row}".PadRight (8).ToCharArray ();
                        }
                        for (var row = origin; row < origin + liveHeight; row++)
                        {
                            terminal.Rows [row] = "LIVE    ".ToCharArray ();
                        }
                        List<string> expected = [.. Enumerable.Range (0, origin).Select (row => $"S{row}"),
                                     .. Enumerable.Range (0, count).Select (row => $"P{row}")];
                        using AnsiOutput output = new (bytes => { terminal.Feed (Encoding.UTF8.GetString (bytes)); return true; }, AppModel.Inline);
                        InlineOutputLayout layout = new (Enumerable.Range (0, count).Select (row => new InlineOutputLine ([new InlineOutputSpan ($"P{row}")])).ToArray ());
                        int newY = ((IInlineOutput)output).InsertAbove (layout, new Rectangle (0, origin, 8, liveHeight), new Size (8, height));
                        Assert.Equal (Math.Min (origin + count, height - liveHeight), newY);
                        List<string> actual = [.. terminal.History, .. terminal.Rows.Select (row => new string (row).TrimEnd ())];
                        Assert.DoesNotContain (actual, line => line.Contains ("LIVE"));
                        Assert.Equal (expected, actual.Where (line => line.Length > 0));
                    }
    }

    [Fact]
    public void DenseRowsResetStylesAndAvoidDisplayErasureOrAlternateBuffer ()
    {
        StringBuilder bytes = new ();
        using AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        InlineOutputLayout layout = new ([new InlineOutputLine ([new InlineOutputSpan ("ab", new Attribute (Color.Red, Color.Blue, TextStyle.Bold)), new InlineOutputSpan ("cd")])]);
        ((IInlineOutput)output).InsertAbove (layout, new Rectangle (0, 0, 4, 1), new Size (4, 2));
        string text = bytes.ToString ();
        Assert.Contains ("ab", text);
        Assert.Contains ("cd", text);
        Assert.Contains ("\u001b[0m", text);
        Assert.DoesNotContain ("[2J", text);
        Assert.DoesNotContain ("[3J", text);
        Assert.DoesNotContain ("1049", text);
        Assert.EndsWith (EscSeqUtils.CSI_ResetAttributes, text);
    }

    [Fact]
    public void OneColumnExactWrapDoesNotScrollImplicitly ()
    {
        Grid terminal = new (1, 2);
        using AnsiOutput output = new (bytes => { terminal.Feed (Encoding.UTF8.GetString (bytes)); return true; }, AppModel.Inline);
        ((IInlineOutput)output).InsertAbove (new InlineOutputLayout ([new InlineOutputLine ([new InlineOutputSpan ("abc")])]), new Rectangle (0, 0, 1, 1), new Size (1, 2));
        Assert.Equal (["a", "b", "c"], terminal.History.Concat (terminal.Rows.Select (row => new string (row))).Where (row => row != " "));
    }

    // CoPilot - GPT-6
    [Fact]
    public void ShrinkErasesReleasedRowsAndPreservesUnownedRowsWithoutScrolling ()
    {
        Grid terminal = new (20, 12);
        terminal.Rows [0] = "SHELL BEFORE".PadRight (20).ToCharArray ();
        terminal.Rows [10] = "UNOWNED BELOW".PadRight (20).ToCharArray ();
        AnsiOutput output = new (chunk => { terminal.Feed (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (20, 12);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = new Point (0, 2) };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "preview\npreview\npreview\npreview\npreview\npreview" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Assert.Contains ("preview", new string (terminal.Rows [7]));
        composer.Text = "ready";
        composer.Height = 2;

        app.LayoutAndDraw ();

        Assert.Empty (terminal.History);
        Assert.Equal ("SHELL BEFORE", new string (terminal.Rows [0]).TrimEnd ());
        Assert.Equal ("UNOWNED BELOW", new string (terminal.Rows [10]).TrimEnd ());
        Assert.Equal ("ready", new string (terminal.Rows [2]).TrimEnd ());
        Assert.All (terminal.Rows.Skip (4).Take (4), row => Assert.Equal (string.Empty, new string (row).TrimEnd ()));
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Fact]
    public void ShrinkAndCommitInOnePass_NeverScrollsPreviewIntoHistory ()
    {
        Grid terminal = new (20, 8);
        terminal.Rows [0] = "SHELL ZERO".PadRight (20).ToCharArray ();
        terminal.Rows [1] = "SHELL ONE".PadRight (20).ToCharArray ();
        AnsiOutput output = new (chunk => { terminal.Feed (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (20, 8);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = new Point (0, 2) };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "preview\npreview\npreview\npreview\npreview\npreview" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        composer.Text = "ready";
        composer.Height = 2;
        Task completed = app.PrintAboveAsync (owner, "DONE", TestContext.Current.CancellationToken);

        app.LayoutAndDraw ();

        Assert.True (completed.IsCompletedSuccessfully);
        Assert.Equal (new Rectangle (0, 3, 20, 2), app.Screen);
        for (int index = 0; index < 12; index++)
        {
            Task print = app.PrintAboveAsync (owner, $"P{index}", TestContext.Current.CancellationToken);
            app.LayoutAndDraw ();
            Assert.True (print.IsCompletedSuccessfully);
        }

        List<string> lines = [.. terminal.History, .. terminal.Rows.Select (row => new string (row).TrimEnd ())];
        Assert.DoesNotContain (lines, line => line.Contains ("preview"));
        Assert.DoesNotContain (terminal.History, line => line.Contains ("ready"));
        Assert.Equal (["SHELL ZERO", "SHELL ONE", "DONE", .. Enumerable.Range (0, 12).Select (index => $"P{index}"), "ready"], lines.Where (line => line.Length > 0));
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Fact]
    public void RegrowthAtBottom_ScrollsFromTerminalBottomRegardlessOfCaretPosition ()
    {
        Grid terminal = new (20, 8);
        AnsiOutput output = new (chunk => { terminal.Feed (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (20, 8);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = Point.Empty };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "ready" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        composer.Height = 2;
        app.LayoutAndDraw ();
        for (int index = 0; index < 9; index++)
        {
            _ = app.PrintAboveAsync (owner, $"P{index}", TestContext.Current.CancellationToken);
            app.LayoutAndDraw ();
        }
        output.SetCursor (new Cursor { Position = Point.Empty });

        composer.Height = 6;
        app.LayoutAndDraw ();
        composer.Height = 2;
        app.LayoutAndDraw ();

        List<string> lines = [.. terminal.History, .. terminal.Rows.Select (row => new string (row).TrimEnd ())];
        Assert.Equal ([.. Enumerable.Range (0, 9).Select (index => $"P{index}"), "ready"], lines.Where (line => line.Length > 0));
        Assert.DoesNotContain (terminal.History, line => line.Contains ("ready"));
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Fact]
    public void ModalRegrowthAfterShrink_RendersAtScrolledOriginWithoutClipping ()
    {
        Grid terminal = new (20, 8);
        terminal.Rows [0] = "SHELL ZERO".PadRight (20).ToCharArray ();
        terminal.Rows [1] = "SHELL ONE".PadRight (20).ToCharArray ();
        AnsiOutput output = new (chunk => { terminal.Feed (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (20, 8);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = new Point (0, 2) };
        app.Init ();
        using Runnable outer = new () { Width = Dim.Fill (), Height = 6, Text = "outer" };
        SessionToken outerOwner = app.Begin (outer)!;
        app.LayoutAndDraw ();
        using Runnable modal = new () { Y = 1, Width = Dim.Fill (), Height = 4 };
        SessionToken modalOwner = app.Begin (modal)!;
        outer.Height = 2;
        app.LayoutAndDraw ();
        modal.Height = 1;
        app.LayoutAndDraw ();
        Assert.Equal (2, app.Screen.Height);

        modal.Height = 7;
        modal.Text = "M0\nM1\nM2\nM3\nM4\nM5\nM6";
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 0, 20, 8), app.Screen);
        Assert.Equal (["SHELL ZERO", "SHELL ONE"], terminal.History);
        Assert.Equal (["outer", .. Enumerable.Range (0, 7).Select (index => $"M{index}")], terminal.Rows.Select (row => new string (row).TrimEnd ()));
        app.End (modalOwner);
        app.End (outerOwner);
    }

    // CoPilot - GPT-6
    [Fact]
    public void BottomAnchoredRegrowthAfterShrink_RendersEntireFrame ()
    {
        Grid terminal = new (20, 8);
        terminal.Rows [0] = "SHELL ZERO".PadRight (20).ToCharArray ();
        terminal.Rows [1] = "SHELL ONE".PadRight (20).ToCharArray ();
        AnsiOutput output = new (chunk => { terminal.Feed (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (20, 8);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output)) { AppModel = AppModel.Inline, ForceInlinePosition = new Point (0, 2) };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6 };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        composer.Height = 2;
        app.LayoutAndDraw ();
        Assert.Equal (2, app.Screen.Height);

        composer.Y = Pos.AnchorEnd ();
        composer.Height = 6;
        composer.Text = "R0\nR1\nR2\nR3\nR4\nR5";
        app.LayoutAndDraw ();

        Assert.Equal (new Rectangle (0, 2, 20, 6), app.Screen);
        Assert.Empty (terminal.History);
        Assert.Equal (["SHELL ZERO", "SHELL ONE", .. Enumerable.Range (0, 6).Select (index => $"R{index}")], terminal.Rows.Select (row => new string (row).TrimEnd ()));
        app.End (owner);
    }

    [Theory]
    [InlineData (20, 12)]
    [InlineData (20, 10)]
    public void Resize_PreservesShellCompletedMessagesAndUnownedRows (int width, int height)
    {
        Grid terminal = new (30, 12);
        terminal.Feed ("\u001b[1;1HSHELL ZERO\u001b[2;1HSHELL ONE\u001b[12;1HUNOWNED BELOW");
        StringBuilder bytes = new ();
        AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (30, 12);
        ISizeMonitor monitor = Mock.Of<ISizeMonitor> (size => size.InitialSizeReceived == true);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: monitor))
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 6)
        };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 3, Text = "LIVE" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        Task first = app.PrintAboveAsync (owner, "done one", TestContext.Current.CancellationToken);
        app.LayoutAndDraw ();
        Assert.True (first.IsCompletedSuccessfully);
        output.SetCursor (new Cursor { Position = new Point (0, 1), Style = CursorStyle.SteadyBar });
        terminal.Feed (bytes.ToString ());
        bytes.Clear ();

        terminal.Resize (width, height);
        app.Driver!.SetScreenSize (width, height);
        Task second = app.PrintAboveAsync (owner, "done two", TestContext.Current.CancellationToken);
        app.LayoutAndDraw ();
        Assert.False (second.IsCompleted);
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        AnsiResponseParser<char> parser = (AnsiResponseParser<char>)((DriverImpl)app.Driver).GetInputProcessor ().GetParser ();
        string response = $"\u001b[{terminal.CursorRow + 1};1R";
        parser.ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());
        app.LayoutAndDraw ();
        terminal.Feed (bytes.ToString ());

        Assert.True (second.IsCompletedSuccessfully);
        string [] lines = [.. terminal.History, .. terminal.Rows.Select (row => new string (row).TrimEnd ())];
        Assert.Single (lines, line => line == "SHELL ZERO");
        Assert.Single (lines, line => line == "SHELL ONE");
        Assert.Single (lines, line => line == "done one");
        Assert.Single (lines, line => line == "done two");
        Assert.Equal ("UNOWNED BELOW", lines.Last ());
        Assert.DoesNotContain (terminal.History, line => line.Contains ("LIVE", StringComparison.Ordinal));
        Assert.DoesNotContain ("[2J", bytes.ToString ());
        Assert.DoesNotContain ("[3J", bytes.ToString ());
        app.End (owner);
    }

    [Fact]
    public void BottomClippingResize_ErasesOnlySurvivingLiveRowsBeforeScrolling ()
    {
        Grid terminal = new (30, 12);
        terminal.Feed ("\u001b[1;1HSHELL ZERO\u001b[2;1HSHELL ONE\u001b[3;1HSHELL TWO");
        StringBuilder bytes = new ();
        AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (30, 12);
        ISizeMonitor monitor = Mock.Of<ISizeMonitor> (size => size.InitialSizeReceived == true);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: monitor))
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 3)
        };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "LIVE0\nLIVE1\nLIVE2\nLIVE3\nLIVE4\nLIVE5" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = Point.Empty, Style = CursorStyle.SteadyBar });
        terminal.Feed (bytes.ToString ());
        bytes.Clear ();
        terminal.Resize (20, 6, clipBottom: true);

        app.Driver!.SetScreenSize (20, 6);
        AnsiResponseParser<char> parser = (AnsiResponseParser<char>)((DriverImpl)app.Driver).GetInputProcessor ().GetParser ();
        parser.ProcessInput ("\u001b[4;1R".Select (character => Tuple.Create (character, character)).ToArray ());
        app.LayoutAndDraw ();
        string writes = bytes.ToString ();
        terminal.Feed (writes);

        Assert.Equal (["SHELL ZERO", "SHELL ONE", "SHELL TWO"], terminal.History);
        Assert.DoesNotContain (terminal.History, line => line.Contains ("LIVE", StringComparison.Ordinal));
        Assert.Equal (new Rectangle (0, 0, 20, 6), app.Screen);
        string beforeScrolling = writes [..writes.IndexOf ('\n')];
        Assert.DoesNotContain ("[1;1H", beforeScrolling);
        Assert.DoesNotContain ("[2;1H", beforeScrolling);
        Assert.DoesNotContain ("[3;1H", beforeScrolling);
        Assert.Equal (["LIVE0", "LIVE1", "LIVE2", "LIVE3", "LIVE4", "LIVE5"], terminal.Rows.Select (row => new string (row).TrimEnd ()));
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (5, 6)]
    [InlineData (1, 4)]
    [InlineData (0, 3)]
    [InlineData (0, 1)]
    public void BottomClippingResize_ClampedCursorNeverErasesPrecedingOutput (int cursorOffset, int height)
    {
        Grid terminal = new (30, 12);
        terminal.Feed ("\u001b[1;1HSHELL ZERO\u001b[2;1HSHELL ONE\u001b[3;1HSHELL TWO");
        StringBuilder bytes = new ();
        AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (30, 12);
        ISizeMonitor monitor = Mock.Of<ISizeMonitor> (size => size.InitialSizeReceived == true);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: monitor))
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 3)
        };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "LIVE0\nLIVE1\nLIVE2\nLIVE3\nLIVE4\nLIVE5" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, cursorOffset), Style = CursorStyle.SteadyBar });
        terminal.Feed (bytes.ToString ());
        Assert.Equal (3 + cursorOffset, terminal.CursorRow);
        bytes.Clear ();
        terminal.Resize (20, height, clipBottom: true);
        string [] surviving = [.. terminal.Rows.Select (row => new string (row))];

        app.Driver!.SetScreenSize (20, height);
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        AnsiResponseParser<char> parser = (AnsiResponseParser<char>)((DriverImpl)app.Driver).GetInputProcessor ().GetParser ();
        string response = $"\u001b[{terminal.CursorRow + 1};1R";
        parser.ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());
        app.LayoutAndDraw ();
        terminal.Feed (bytes.ToString ());

        Assert.True (pending.IsFaulted);
        Assert.False (output.IsInlinePlacementKnown);
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Assert.Equal (surviving, terminal.Rows.Select (row => new string (row)));
        Assert.Empty (terminal.History);
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (0, false, true, 18)]
    [InlineData (0, true, true, 18)]
    [InlineData (1, false, true, 18)]
    [InlineData (2, false, true, 18)]
    [InlineData (0, false, false, 18)]
    [InlineData (1, false, false, 18)]
    [InlineData (2, false, false, 18)]
    [InlineData (1, false, true, 12)]
    [InlineData (2, false, true, 12)]
    public void ShrinkThenEnlarge_NeverForgetsPossibleClamping (int observation, bool replyBeforeEnlarge, bool clipBottom, int finalHeight)
    {
        Grid terminal = new (30, 12);
        terminal.Feed ("\u001b[1;1HSHELL ZERO\u001b[2;1HSHELL ONE\u001b[3;1HSHELL TWO\u001b[10;1HUNOWNED0\u001b[11;1HUNOWNED1\u001b[12;1HUNOWNED2");
        StringBuilder bytes = new ();
        AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (30, 12);
        ISizeMonitor monitor = Mock.Of<ISizeMonitor> (size => size.InitialSizeReceived == true);
        VirtualTimeProvider time = new ();
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: monitor), time)
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 3)
        };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "LIVE0\nLIVE1\nLIVE2\nLIVE3\nLIVE4\nLIVE5" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        terminal.Feed (bytes.ToString ());
        Assert.Equal (3, terminal.CursorRow);
        bytes.Clear ();
        AnsiResponseParser<char> parser = (AnsiResponseParser<char>)((DriverImpl)app.Driver!).GetInputProcessor ().GetParser ();
        ((DriverImpl)app.Driver).AnsiRequestScheduler.Now = () => time.Now;
        if (observation != 0)
        {
            app.Driver.SetScreenSize (20, 12);
        }
        int smallHeight = clipBottom ? 3 : 4;
        terminal.Resize (20, smallHeight, clipBottom: clipBottom);
        string response = $"\u001b[{terminal.CursorRow + 1};1R";
        Size nativeSize = new (20, smallHeight);
        if (observation == 0)
        {
            app.Driver.SetScreenSize (20, smallHeight);
        }
        else
        {
            output.NativeSizeQuery = () => nativeSize;
        }
        if (replyBeforeEnlarge || observation == 2)
        {
            parser.ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());
        }
        if (observation == 1)
        {
            app.LayoutAndDraw ();
        }
        terminal.Resize (20, finalHeight, restoreHistory: !clipBottom);
        nativeSize = new Size (20, finalHeight);
        string [] surviving = [.. terminal.Rows.Select (row => new string (row))];
        Assert.Equal ("SHELL TWO", surviving [2].TrimEnd ());
        if (!clipBottom)
        {
            Assert.Equal ("UNOWNED0", surviving [9].TrimEnd ());
        }
        if (finalHeight != 12)
        {
            app.Driver.SetScreenSize (20, finalHeight);
        }
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        if (!replyBeforeEnlarge && observation != 2)
        {
            parser.ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());
        }
        time.Advance (TimeSpan.FromMilliseconds (200));
        app.LayoutAndDraw ();
        string replacement = $"\u001b[{terminal.CursorRow + 1};1R";
        parser.ProcessInput (replacement.Select (character => Tuple.Create (character, character)).ToArray ());
        app.LayoutAndDraw ();
        terminal.Feed (bytes.ToString ());

        Assert.True (pending.IsFaulted);
        Assert.False (output.IsInlinePlacementKnown);
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Assert.Equal (surviving, terminal.Rows.Select (row => new string (row)));
        Assert.Empty (terminal.History);
        app.End (owner);
    }

    // CoPilot - GPT-6
    [Theory]
    [InlineData (0)]
    [InlineData (1)]
    public void TopClippingResize_ClampedCursorNeverErasesUnownedRowsBelow (int cursorOffset)
    {
        Grid terminal = new (30, 12);
        terminal.Feed ("\u001b[1;1HSHELL ZERO\u001b[2;1HSHELL ONE\u001b[3;1HSHELL TWO\u001b[10;1HUNOWNED0\u001b[11;1HUNOWNED1\u001b[12;1HUNOWNED2");
        StringBuilder bytes = new ();
        AnsiOutput output = new (chunk => { bytes.Append (Encoding.UTF8.GetString (chunk)); return true; }, AppModel.Inline);
        output.SetSize (30, 12);
        ISizeMonitor monitor = Mock.Of<ISizeMonitor> (size => size.InitialSizeReceived == true);
        using ApplicationImpl app = new (new AnsiComponentFactory (output: output, sizeMonitor: monitor))
        {
            AppModel = AppModel.Inline,
            ForceInlinePosition = new Point (0, 3)
        };
        app.Init ();
        using Runnable composer = new () { Width = Dim.Fill (), Height = 6, Text = "LIVE0\nLIVE1\nLIVE2\nLIVE3\nLIVE4\nLIVE5" };
        SessionToken owner = app.Begin (composer)!;
        app.LayoutAndDraw ();
        output.SetCursor (new Cursor { Position = new Point (0, cursorOffset), Style = CursorStyle.SteadyBar });
        terminal.Feed (bytes.ToString ());
        bytes.Clear ();
        terminal.Resize (20, 4);
        string [] surviving = [.. terminal.Rows.Select (row => new string (row).TrimEnd ())];
        string [] history = [.. terminal.History];
        Assert.Equal (["LIVE5", "UNOWNED0", "UNOWNED1", "UNOWNED2"], surviving);

        app.Driver!.SetScreenSize (20, 4);
        Task pending = app.PrintAboveAsync (owner, "must not print", TestContext.Current.CancellationToken);
        AnsiResponseParser<char> parser = (AnsiResponseParser<char>)((DriverImpl)app.Driver).GetInputProcessor ().GetParser ();
        string response = $"\u001b[{terminal.CursorRow + 1};1R";
        parser.ProcessInput (response.Select (character => Tuple.Create (character, character)).ToArray ());
        app.LayoutAndDraw ();
        terminal.Feed (bytes.ToString ());

        Assert.True (pending.IsFaulted);
        Assert.False (output.IsInlinePlacementKnown);
        Assert.Equal ("\u001b[6n", bytes.ToString ());
        Assert.Equal (surviving, terminal.Rows.Select (row => new string (row).TrimEnd ()));
        Assert.Equal (history, terminal.History);
        app.End (owner);
    }

    // Minimal VT state model for the emitted subset, including delayed autowrap and actual scroll history.
    private sealed class Grid
    {
        internal List<char []> Rows { get; } = [];
        internal List<string> History { get; } = [];
        private int _width;
        private int _height;
        private int _row;
        private int _column;
        private bool _wrapPending;

        internal int CursorRow => _row;

        internal void Resize (int width, int height, bool clipBottom = false, bool restoreHistory = false)
        {
            int removed = Math.Max (0, _height - height);
            for (var index = 0; index < removed; index++)
            {
                if (clipBottom)
                {
                    Rows.RemoveAt (Rows.Count - 1);
                    continue;
                }
                History.Add (new string (Rows [0]).TrimEnd ());
                Rows.RemoveAt (0);
            }
            if (restoreHistory && height > _height)
            {
                int restored = Math.Min (height - _height, History.Count);
                Rows.InsertRange (0, History.TakeLast (restored).Select (row => row.PadRight (_width).ToCharArray ()));
                History.RemoveRange (History.Count - restored, restored);
                _row += restored;
            }
            for (var index = 0; index < Rows.Count; index++)
            {
                char [] resized = new string (' ', width).ToCharArray ();
                Array.Copy (Rows [index], resized, Math.Min (_width, width));
                Rows [index] = resized;
            }
            while (Rows.Count < height)
            {
                Rows.Add (new string (' ', width).ToCharArray ());
            }
            _row = Math.Clamp (_row - (clipBottom ? 0 : removed), 0, height - 1);
            _column = Math.Min (_column, width - 1);
            _width = width;
            _height = height;
            _wrapPending = false;
        }

        internal Grid (int width, int height)
        {
            _width = width;
            _height = height;
            for (var i = 0; i < height; i++)
            {
                Rows.Add (new string (' ', width).ToCharArray ());
            }
        }

        internal void Feed (string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                char c = text [i];
                if (c == '\u001b' && text [i + 1] == ']')
                {
                    i = text.IndexOf ("\u001b\\", i, StringComparison.Ordinal) + 1;
                    continue;
                }
                if (c == '\u001b' && text [i + 1] == '[')
                {
                    int start = i + 2;
                    i = start;
                    while (text [i] is not (>= '@' and <= '~'))
                    {
                        i++;
                    }
                    string parameters = text [start..i];
                    if (text [i] == 'H')
                    {
                        int [] values = parameters.Split (';').Select (int.Parse).ToArray ();
                        _row = values [0] - 1;
                        _column = values [1] - 1;
                        _wrapPending = false;
                    }
                    if (text [i] == 'K')
                    {
                        Assert.Equal ("2", parameters);
                        Array.Fill (Rows [_row], ' ');
                    }
                    continue;
                }
                if (c == '\r')
                {
                    _column = 0;
                    _wrapPending = false;
                    continue;
                }
                if (c == '\n')
                {
                    Advance ();
                    continue;
                }
                if (_wrapPending)
                {
                    _column = 0;
                    Advance ();
                }
                Rows [_row] [_column] = c;
                _wrapPending = ++_column == _width;
                if (_wrapPending)
                {
                    _column = _width - 1;
                }
            }
        }

        private void Advance ()
        {
            _wrapPending = false;
            if (++_row < _height)
            {
                return;
            }
            History.Add (new string (Rows [0]).TrimEnd ());
            Rows.RemoveAt (0);
            Rows.Add (new string (' ', _width).ToCharArray ());
            _row = _height - 1;
        }
    }
}
