using System.Text;
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

    // Minimal VT state model for the emitted subset, including delayed autowrap and actual scroll history.
    private sealed class Grid
    {
        internal List<char []> Rows { get; } = [];
        internal List<string> History { get; } = [];
        private readonly int _width;
        private readonly int _height;
        private int _row;
        private int _column;
        private bool _wrapPending;

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
