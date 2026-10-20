using System.Text;
using Markdig;
using TgMarkdown = Terminal.Gui.Views.Markdown;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownParsedContentTests
{
    [Theory]
    [InlineData ("``a`b``", "a`b")]
    [InlineData ("`` `x` ``", "`x`")]
    [InlineData ("\\*literal\\*", "*literal*")]
    [InlineData ("[Testing [Header]](https://example.com)", "Testing [Header]")]
    [InlineData ("~~deleted~~", "deleted")]
    [InlineData ("**bold *inner***", "bold inner")]
    [InlineData ("a\\|b", "a|b")]
    public void Table_Cell_Renders_Parsed_Text (string source, string expected)
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, source, out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());

        Assert.Equal ($"│ {expected}", ReadRow (app.Driver!, table, 3).TrimEnd (' ', '│'));
        Assert.Equal (5, table.RenderedHeight);
    }

    [Theory]
    [InlineData ("~~deleted~~", TextStyle.Strikethrough)]
    [InlineData ("**bold**", TextStyle.Bold)]
    [InlineData ("*italic*", TextStyle.Italic)]
    [InlineData ("***both***", TextStyle.Bold | TextStyle.Italic)]
    [InlineData ("[**bold**](https://example.com)", TextStyle.Bold | TextStyle.Underline)]
    public void Table_Cell_Preserves_Text_Style (string source, TextStyle expected)
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, source, out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());
        Point position = table.ViewportToScreen (new Point (2, 3));
        TextStyle actual = app.Driver!.Contents! [position.Y, position.X].Attribute!.Value.Style;

        Assert.Equal (expected, actual & expected);
    }

    [Fact]
    public void Table_Nested_Emphasis_Combines_Styles_At_Inner_Text ()
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, "**bold *inner***", out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());
        Point position = table.ViewportToScreen (new Point (7, 3));
        TextStyle actual = app.Driver!.Contents! [position.Y, position.X].Attribute!.Value.Style;

        Assert.Equal (TextStyle.Bold | TextStyle.Italic, actual & (TextStyle.Bold | TextStyle.Italic));
    }

    [Fact]
    public void Table_Text_Model_Uses_Safe_Code_Delimiters ()
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, "``a`b``", out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());

        Assert.Equal ("``a`b``", table.TableData.Rows [0] [0]);
    }

    [Theory]
    [InlineData (false)]
    [InlineData (true)]
    public void Table_Nested_Label_Activates_Original_Url (bool mouse)
    {
        const string url = "https://example.com/path_(part)";
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, $"[Testing [Header]]({url})", out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());
        List<string> activated = [];
        markdown.LinkClicked += (_, e) =>
                                {
                                    activated.Add (e.Url);
                                    e.Handled = true;
                                };

        if (mouse)
        {
            table.NewMouseEvent (new Mouse { Position = new Point (2, 3), Flags = MouseFlags.LeftButtonClicked });
        }
        else
        {
            table.SetFocus ();
            table.InvokeCommand (Command.Accept);
        }

        Assert.Equal ([url], activated);
    }

    [Fact]
    public void Table_Wrapping_Preserves_Styles_And_Original_Link ()
    {
        const string url = "https://example.com/path_(part)";
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, $"[**abcdefghij**]({url})", out TgMarkdown markdown, 9);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());
        Assert.True (table.RenderedHeight > 5);
        Point position = table.ViewportToScreen (new Point (2, 4));
        TextStyle actual = app.Driver!.Contents! [position.Y, position.X].Attribute!.Value.Style;
        Assert.Equal (TextStyle.Bold | TextStyle.Underline, actual & (TextStyle.Bold | TextStyle.Underline));
        string? activated = null;
        markdown.LinkClicked += (_, e) =>
                                {
                                    activated = e.Url;
                                    e.Handled = true;
                                };

        table.NewMouseEvent (new Mouse { Position = new Point (2, 4), Flags = MouseFlags.LeftButtonClicked });

        Assert.Equal (url, activated);
    }

    [Fact]
    public void TableData_Reassignment_Replaces_Parsed_Content ()
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        using Runnable window = BeginTable (app, "~~old~~", out TgMarkdown markdown);
        MarkdownTable table = Assert.Single (markdown.SubViews.OfType<MarkdownTable> ());
        table.TableData = new (["Cell"], [Alignment.Start], [ ["**new**"]]);
        app.LayoutAndDraw ();

        Assert.Equal ("│ new", ReadRow (app.Driver!, table, 3).TrimEnd (' ', '│'));
    }

    [Theory]
    [InlineData ("NOTE")]
    [InlineData ("TIP")]
    [InlineData ("IMPORTANT")]
    [InlineData ("WARNING")]
    [InlineData ("CAUTION")]
    public void Alert_Kind_Is_Visible_And_Bold (string kind)
    {
        using IApplication app = Application.Create ().Init (DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize (50, 10);
        using Runnable window = new () { Width = Dim.Fill (), Height = Dim.Fill (), BorderStyle = LineStyle.None };
        TgMarkdown markdown = new () { Text = $"> [!{kind}]\n> Critical **action**", Width = Dim.Fill (), Height = Dim.Fill () };
        window.Add (markdown);
        app.Begin (window);
        app.LayoutAndDraw ();

        Assert.StartsWith ($"> {kind}", ReadRow (app.Driver, markdown, 0));
        Assert.StartsWith ("> Critical action", ReadRow (app.Driver, markdown, 1));
        Point position = markdown.ViewportToScreen (new Point (2, 0));
        Assert.True (app.Driver.Contents! [position.Y, position.X].Attribute!.Value.Style.HasFlag (TextStyle.Bold));
    }

    [Fact]
    public void Custom_Pipeline_Nested_Alert_Preserves_Kind_And_Quote_Prefix ()
    {
        MarkdownPipeline pipeline = new MarkdownPipelineBuilder ().UseAlertBlocks (allowNestedAlerts: true).Build ();
        using TgMarkdown markdown = new () { MarkdownPipeline = pipeline };
        string ansi = markdown.RenderToAnsi ("> outer\n>\n> > [!WARNING]\n> > nested");
        string plain = System.Text.RegularExpressions.Regex.Replace (ansi, @"\x1b\[[0-?]*[ -/]*[@-~]", string.Empty);

        Assert.Contains ("> > WARNING", plain);
        Assert.Contains ("> > nested", plain);
    }

    private static Runnable BeginTable (IApplication app, string source, out TgMarkdown markdown, int width = 60)
    {
        app.Driver!.SetScreenSize (width, 20);
        Runnable window = new () { Width = Dim.Fill (), Height = Dim.Fill (), BorderStyle = LineStyle.None };
        markdown = new () { Text = $"| Cell |\n| --- |\n| {source} |", Width = Dim.Fill (), Height = Dim.Fill () };
        window.Add (markdown);
        app.Begin (window);
        app.LayoutAndDraw ();

        return window;
    }

    private static string ReadRow (IDriver driver, View view, int row)
    {
        Point position = view.ViewportToScreen (new Point (0, row));
        StringBuilder text = new ();

        for (int x = position.X; x < position.X + view.Viewport.Width; x++)
        {
            text.Append (driver.Contents! [position.Y, x].Grapheme);
        }

        return text.ToString ();
    }
}
