namespace Terminal.Gui.Views;

internal sealed record InlineRun (
    string text,
    MarkdownStyleRole styleRole,
    string? url = null,
    string? imageSource = null,
    Attribute? attribute = null,
    VisualRole? role = null)
{
    public string Text { get; init; } = text;
    public MarkdownStyleRole StyleRole { get; init; } = styleRole;
    public string? Url { get; init; } = url;
    public string? ImageSource { get; init; } = imageSource;
    public Attribute? Attribute { get; init; } = attribute;
    public VisualRole? Role { get; init; } = role;
    public TextStyle AdditionalStyle { get; init; }

    public StyledSegment ToStyledSegment (string? text = null) =>
        new (text ?? Text, StyleRole, Url, ImageSource, Attribute, Role)
        {
            AdditionalStyle = AdditionalStyle
        };

    public static InlineRun FromStyledSegment (StyledSegment segment) =>
        new (segment.Text, segment.StyleRole, segment.Url, segment.ImageSource, segment.Attribute, segment.Role)
        {
            AdditionalStyle = segment.AdditionalStyle
        };
}
