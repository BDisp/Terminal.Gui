using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TgMarkdown = Terminal.Gui.Views.Markdown;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownTableSerializationTests
{
    [Fact]
    public void Custom_Pipeline_Table_Copy_Keeps_Ordinary_Punctuation_Readable ()
    {
        using TgMarkdown view = new ()
        {
            MarkdownPipeline = new MarkdownPipelineBuilder ().UsePipeTables ().Build (),
            Text = "before\n\n| H |\n| --- |\n| Hello, world. |\n\nafter",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        view.NewMouseEvent (new Mouse
        {
            Position = new Point (0, table.Frame.Y),
            Flags = MouseFlags.LeftButtonPressed
        });
        view.NewMouseEvent (new Mouse
        {
            Position = new Point (59, table.Frame.Y + table.RenderedHeight - 1),
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport
        });
        string copied = Assert.IsType<string> (view.SelectedText);
        TableData data = Assert.IsType<TableData> (TableData.TryParse (copied.Split ('\n')));

        Assert.Contains ("Hello, world.", copied);
        Assert.Equal ("Hello, world.", string.Concat (MarkdownInlineParser.ParseInlines (data.Rows [0] [0], MarkdownStyleRole.Normal).Select (run => run.Text)));
    }

    [Theory]
    [InlineData (false, false)]
    [InlineData (true, false)]
    [InlineData (false, true)]
    public void Multiline_Grid_Cell_Copy_Preserves_Separators (bool separateParagraphs, bool hardBreak)
    {
        string blank = separateParagraphs ? "|     |\n" : string.Empty;
        string first = hardBreak ? "| foo\\|" : "| foo |";
        using TgMarkdown view = new ()
        {
            Text = $"before\n\n+-----+\n| H   |\n+=====+\n{first}\n{blank}| bar |\n+-----+\n\nafter",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        view.NewMouseEvent (new Mouse
        {
            Position = new Point (0, table.Frame.Y),
            Flags = MouseFlags.LeftButtonPressed
        });
        view.NewMouseEvent (new Mouse
        {
            Position = new Point (59, table.Frame.Y + table.RenderedHeight - 1),
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport
        });
        TableData copied = Assert.IsType<TableData> (TableData.TryParse (Assert.IsType<string> (view.SelectedText).Split ('\n')));
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (copied.Rows [0] [0], MarkdownStyleRole.Normal);

        Assert.Equal (hardBreak ? "foo\nbar" : "foo bar", string.Concat (runs.Select (run => run.Text)));
    }

    [Theory]
    [InlineData (" a|b ")]
    [InlineData ("`a|b`")]
    public void Grid_Cell_Pipes_Do_Not_Create_Extra_Clipboard_Columns (string cell)
    {
        using TgMarkdown view = new ()
        {
            Text = $"+-----+\n| H   |\n+=====+\n|{cell}|\n+-----+",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        TableData copied = Assert.IsType<TableData> (TableData.TryParse (table.Text.Split ('\n')));
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (Assert.Single (copied.Rows [0]), MarkdownStyleRole.Normal);

        Assert.Equal (1, copied.ColumnCount);
        Assert.Equal ("a|b", string.Concat (runs.Select (run => run.Text)));
    }

    [Theory]
    [InlineData ("[label](<path with space> \"a title\")", "path with space", "a title")]
    [InlineData ("[label](u \"a title\")", "u", "a title")]
    [InlineData ("![label](<image with space> 'a title')", "image with space", "a title")]
    [InlineData ("[label][ref]", "path with space", "reference title")]
    [InlineData ("[label](u \"\")", "u", "")]
    [InlineData ("[label](u \"a \\\"quote\\\" \\| title\")", "u", "a \"quote\" | title")]
    public void Serialized_Link_Preserves_Destination_And_Title (string cell, string url, string title)
    {
        using TgMarkdown view = new ()
        {
            Text = $"| H |\n| --- |\n| {cell} |\n\n[ref]: <path with space> \"reference title\"",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        MarkdownDocument reparsed = Markdig.Markdown.Parse (table.TableData.Rows [0] [0]);
        LinkInline link = Assert.Single (reparsed.Descendants<LinkInline> ());

        Assert.Equal (url, link.Url);
        Assert.Equal (title, link.Title);
    }

    [Fact]
    public void Custom_Pipeline_Literal_Domain_Does_Not_Gain_A_Link ()
    {
        using TgMarkdown view = new ()
        {
            MarkdownPipeline = new MarkdownPipelineBuilder ().UsePipeTables ().Build (),
            Text = "| H |\n| --- |\n| www.example.com |",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (table.TableData.Rows [0] [0], MarkdownStyleRole.Normal);

        Assert.Equal ("www.example.com", string.Concat (runs.Select (run => run.Text)));
        Assert.All (runs, run => Assert.Null (run.Url));
    }

    [Theory]
    [InlineData ("www\\.example.com")]
    [InlineData ("www&#46;example.com")]
    public void Literal_Domain_Remains_Literal_After_Reparsing (string source)
    {
        using TgMarkdown view = new ()
        {
            Text = $"| H |\n| --- |\n| {source} |",
            Width = 60,
            Height = 20
        };
        view.Layout ();
        MarkdownTable table = Assert.Single (view.SubViews.OfType<MarkdownTable> ());
        List<InlineRun> runs = MarkdownInlineParser.ParseInlines (table.TableData.Rows [0] [0], MarkdownStyleRole.Normal);

        Assert.Equal ("www.example.com", string.Concat (runs.Select (run => run.Text)));
        Assert.All (runs, run => Assert.Null (run.Url));
    }
}
