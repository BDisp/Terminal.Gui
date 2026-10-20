namespace Terminal.Gui.Views;

/// <summary>Parsed display segments retained separately from the table's Markdown text representation.</summary>
internal sealed class MarkdownTableContent (List<StyledSegment> [] headers, List<StyledSegment> [] [] rows)
{
    public List<StyledSegment> [] Headers { get; } = headers;
    public List<StyledSegment> [] [] Rows { get; } = rows;
}
