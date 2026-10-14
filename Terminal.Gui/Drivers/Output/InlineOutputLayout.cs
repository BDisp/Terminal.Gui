namespace Terminal.Gui.Drivers;

// Immutable request preparation; enumerating physical rows uses only one row of scratch storage.
internal sealed class InlineOutputLayout
{
    internal const int MaximumUnits = 4 * 1024 * 1024;
    private readonly InlineOutputLine [] _lines;
    internal int Units { get; }
    internal bool IsEmpty => _lines.Length == 0;

    internal InlineOutputLayout (IReadOnlyList<InlineOutputLine> lines)
    {
        ArgumentNullException.ThrowIfNull (lines);
        List<InlineOutputLine> snapshot = [];
        var units = 0;
        foreach (InlineOutputLine line in lines)
        {
            ArgumentNullException.ThrowIfNull (line);
            units = checked(units + 1);
            if (units > MaximumUnits)
            {
                throw new InvalidOperationException ("Inline output payload is too large.");
            }
            StringBuilder text = new ();
            foreach (InlineOutputSpan span in line.Spans)
            {
                ArgumentNullException.ThrowIfNull (span);
                ArgumentNullException.ThrowIfNull (span.Text);
                units = checked(units + Math.Max (1, span.Text.Length));
                if (units > MaximumUnits)
                {
                    throw new InvalidOperationException ("Inline output payload is too large. Submit smaller awaited batches.");
                }
                text.Append (span.Text);
            }
            ValidateText (text.ToString ());
            snapshot.Add (new InlineOutputLine (line.Spans));
        }
        if (units > MaximumUnits)
        {
            throw new InvalidOperationException ("Inline output payload is too large.");
        }
        _lines = snapshot.ToArray ();
        Units = units;
    }

    private static void ValidateText (string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            char c = text [i];
            if ((c < ' ' && c != '\t') || c is >= '\u007f' and <= '\u009f')
            {
                throw new ArgumentException ("Inline text cannot contain terminal controls or line breaks.");
            }
            if (char.IsHighSurrogate (c) && i + 1 < text.Length && char.IsLowSurrogate (text [i + 1]))
            {
                i++;
                continue;
            }
            if (char.IsSurrogate (c))
            {
                throw new ArgumentException ("Inline text must contain valid UTF-16.");
            }
        }
    }

    internal int CountRows (int width)
    {
        var count = 0;
        foreach (InlineOutputLine _ in Rows (width))
        {
            count = checked(count + 1);
        }
        return count;
    }

    internal IEnumerable<InlineOutputLine> Rows (int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero (width);
        foreach (InlineOutputLine line in _lines)
        {
            string text = string.Concat (line.Spans.Select (span => span.Text));
            var offset = 0;
            var spanIndex = 0;
            var spanEnd = line.Spans.Count > 0 ? line.Spans [0].Text.Length : 0;
            var column = 0;
            List<InlineOutputSpan> row = [];
            foreach (string element in GraphemeHelper.GetGraphemes (text))
            {
                while (spanIndex + 1 < line.Spans.Count && offset >= spanEnd)
                {
                    spanIndex++;
                    spanEnd += line.Spans [spanIndex].Text.Length;
                }
                Attribute? style = line.Spans [spanIndex].Attribute;
                offset += element.Length;
                if (column == width)
                {
                    yield return new InlineOutputLine (row);
                    row = [];
                    column = 0;
                }
                if (element == "\t")
                {
                    int spaces = 8 - column % 8;
                    for (var i = 0; i < spaces; i++)
                    {
                        if (column == width)
                        {
                            yield return new InlineOutputLine (row);
                            row = [];
                            column = 0;
                        }
                        row.Add (new InlineOutputSpan (" ", style));
                        column++;
                    }
                    continue;
                }
                string grapheme = element;
                int cells = Math.Max (0, grapheme.GetColumns ());
                if (cells == 0 && column == 0)
                {
                    grapheme = "◌" + grapheme;
                    cells = 1;
                }
                if (cells > width)
                {
                    grapheme = "�";
                    cells = 1;
                }
                if (column + cells > width)
                {
                    yield return new InlineOutputLine (row);
                    row = [];
                    column = 0;
                }
                row.Add (new InlineOutputSpan (grapheme, style));
                column += cells;
            }
            yield return new InlineOutputLine (row);
        }
    }
}
