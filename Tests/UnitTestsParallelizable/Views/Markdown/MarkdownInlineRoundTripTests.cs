using System.Reflection;
using TgMarkdown = Terminal.Gui.Views.Markdown;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownInlineRoundTripTests
{
    [Theory]
    [InlineData ("**a\\|b**", "a|b", MarkdownStyleRole.Strong)]
    [InlineData ("*x\\*y*", "x*y", MarkdownStyleRole.Emphasis)]
    [InlineData ("[Testing \\[Header\\]](https://example.com/path_(part))", "Testing [Header]", MarkdownStyleRole.Link)]
    [InlineData ("![alt\\]text](image_(part).png)", "[alt]text]", MarkdownStyleRole.ImageAlt)]
    public void String_Inlines_Respect_Escaped_Delimiters (string source, string expected, MarkdownStyleRole role)
    {
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (source, MarkdownStyleRole.Normal);

        Assert.Equal (expected, string.Concat (runs.Select (run => run.Text)));
        Assert.All (runs, run => Assert.Equal (role, run.StyleRole));

        if (role == MarkdownStyleRole.Link)
        {
            Assert.All (runs, run => Assert.Equal ("https://example.com/path_(part)", run.Url));
        }

        if (role == MarkdownStyleRole.ImageAlt)
        {
            Assert.Equal ("image_(part).png", Assert.Single (runs).ImageSource);
        }
    }

    [Theory]
    [InlineData ("| a \\|", "a \\|")]
    [InlineData ("| a \\||", "a \\|")]
    [InlineData ("| a \\\\|", "a \\\\")]
    public void Row_Boundaries_Preserve_Escaped_Pipes (string row, string expected)
    {
        TableData data = Assert.IsType<TableData> (TableData.TryParse (["| H |", "| --- |", row]));

        Assert.Equal (expected, data.Rows [0] [0]);
    }

    [Theory]
    [InlineData ("|| H |", 2)]
    [InlineData ("| H ||", 2)]
    [InlineData ("||", 1)]
    public void Row_Boundaries_Remove_Only_One_Outer_Pipe (string header, int columns)
    {
        TableData data = Assert.IsType<TableData> (TableData.TryParse ([header, "| --- | --- |"]));

        Assert.Equal (columns, data.ColumnCount);
    }

    [Theory]
    [InlineData ("snake_case")]
    [InlineData ("[1]")]
    public void Harmless_Literals_Do_Not_Gain_Unnecessary_Escapes (string source)
    {
        using TgMarkdown view = CreateTable (source);
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());

        Assert.Equal (source, table.TableData.Rows [0] [0]);
    }

    [Theory]
    [InlineData ("a\\|b")]
    [InlineData ("**a\\|b**")]
    [InlineData ("\\*literal\\*")]
    [InlineData ("*x\\*y*")]
    [InlineData ("[Testing \\[Header\\]](https://example.com/path_(part))")]
    [InlineData ("[Testing [Header]](https://example.com/path_(part))")]
    [InlineData ("[label](https://example.com/a\\|b)")]
    [InlineData ("[label](https://example.com/a\\))")]
    [InlineData ("**bold *inner***")]
    [InlineData ("``a\\|b`c``")]
    [InlineData ("![alt\\]text](image_(part).png)")]
    [InlineData ("\\~literal\\~")]
    [InlineData ("\\^literal\\^")]
    [InlineData ("\\=\\=literal\\=\\=")]
    public void Table_String_And_Ast_Paths_Produce_Identical_Styled_Content (string source)
    {
        using TgMarkdown view = CreateTable (source);
        MarkdownTable parsed = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        using MarkdownTable standalone = new ()
        {
            TableData = parsed.TableData
        };
        TableData copied = Assert.IsType<TableData> (TableData.TryParse (standalone.Text.Split ('\n')));
        using MarkdownTable reparsed = new ()
        {
            TableData = copied
        };

        Assert.Equal (Expand (GetBody (parsed)), Expand (GetBody (standalone)));
        Assert.Equal (Expand (GetBody (parsed)), Expand (GetBody (reparsed)));
    }

    [Theory]
    [InlineData ("``a`", false)]
    [InlineData ("```x`", false)]
    [InlineData ("``a``", true)]
    [InlineData ("`", false)]
    public void Backtick_Runs_Are_Matched_Whole_Or_Kept_Literal (string source, bool code)
    {
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (source, MarkdownStyleRole.Normal);

        Assert.Equal (code ? "a" : source, string.Concat (runs.Select (run => run.Text)));
        Assert.All (runs, run => Assert.Equal (code ? MarkdownStyleRole.InlineCode : MarkdownStyleRole.Normal, run.StyleRole));
    }

    private static TgMarkdown CreateTable (string cell)
    {
        TgMarkdown view = new ()
        {
            Text = $"| H |\n| --- |\n| {cell} |",
            Width = 60,
            Height = 20
        };
        view.Layout ();

        return view;
    }

    private static List<StyledSegment> GetBody (MarkdownTable table) =>
        ((List<StyledSegment> [] [])typeof (MarkdownTable).GetField ("_rowSegments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue (table)!) [0] [0];

    private static IEnumerable<(string, MarkdownStyleRole, string?, string?, TextStyle, VisualRole?)> Expand (IEnumerable<StyledSegment> segments) =>
        segments.SelectMany (segment => GraphemeHelper.GetGraphemes (segment.Text)
                                                     .Select (grapheme => (grapheme, segment.StyleRole, segment.Url, segment.ImageSource, segment.AdditionalStyle, segment.Role)));
}
