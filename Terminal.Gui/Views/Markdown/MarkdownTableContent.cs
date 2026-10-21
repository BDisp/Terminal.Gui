namespace Terminal.Gui.Views;

/// <summary>Retains parsed table segments and their width bounds independently of the current viewport.</summary>
internal sealed class MarkdownTableContent
{
    public MarkdownTableContent (List<StyledSegment> [] headers, List<StyledSegment> [] [] rows)
    {
        Headers = headers;
        Rows = rows;
        MaximumWidths = new int [headers.Length];
        MinimumWidths = new int [headers.Length];

        for (int column = 0; column < headers.Length; column++)
        {
            MaximumWidths [column] = MarkdownTable.MeasureRenderedWidth (headers [column]);
            MinimumWidths [column] = MarkdownTable.MeasureLongestWord (headers [column]);
        }

        foreach (List<StyledSegment> [] row in rows)
        {
            for (int column = 0; column < headers.Length && column < row.Length; column++)
            {
                MaximumWidths [column] = Math.Max (MaximumWidths [column], MarkdownTable.MeasureRenderedWidth (row [column]));
                MinimumWidths [column] = Math.Max (MinimumWidths [column], MarkdownTable.MeasureLongestWord (row [column]));
            }
        }
    }

    public List<StyledSegment> [] Headers { get; }
    public List<StyledSegment> [] [] Rows { get; }
    public int [] MaximumWidths { get; }
    public int [] MinimumWidths { get; }
}
