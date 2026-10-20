using System.Text.RegularExpressions;
using Markdig;
using TgMarkdown = Terminal.Gui.Views.Markdown;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownReviewRegressionTests
{
    [Theory]
    [InlineData ("- Term\n  : definition", new string [] { "• Term", "    definition" })]
    [InlineData ("- First\n  : one\n  : two\n\n  Second\n  : three", new string [] { "• First", "    one", "    two", "  Second", "    three" })]
    public void Definition_List_In_List_Uses_One_Bullet (string source, string [] expected)
    {
        Assert.Equal (expected, RenderLines (source).Where (line => line.Length > 0));
    }

    [Fact]
    public void Alert_In_List_Uses_One_Bullet ()
    {
        Assert.Equal (["• > NOTE", "  > first", "  > second"], RenderLines ("- > [!NOTE]\n  > first\n  >\n  > second", new MarkdownPipelineBuilder ().UseAlertBlocks (allowNestedAlerts: true).Build ()).Where (line => line.Length > 0));
    }

    private static string [] RenderLines (string source, MarkdownPipeline? pipeline = null)
    {
        using TgMarkdown view = new () { MarkdownPipeline = pipeline };
        string ansi = view.RenderToAnsi (source);
        string plain = Regex.Replace (ansi, @"\x1b\[[0-?]*[ -/]*[@-~]", string.Empty);

        return plain.Replace ("\r\n", "\n").TrimEnd ().Split ('\n').Select (line => line.TrimEnd ()).ToArray ();
    }
}
