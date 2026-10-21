using System.Reflection;
using Markdig;
using Moq;
using TgMarkdown = Terminal.Gui.Views.Markdown;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownParseRecoveryTests
{
    [Fact]
    public void Parse_ArgumentException_Does_Not_Depend_On_Message ()
    {
        int parses = 0;
        MarkdownPipelineBuilder builder = new ();
        builder.DocumentProcessed += _ =>
                                     {
                                         parses++;

                                         throw new ArgumentException ("A different parser failure message");
                                     };
        using TgMarkdown view = new ()
        {
            Text = "**literal**",
            MarkdownPipeline = builder.Build (),
            Width = 60,
            Height = 10
        };
        view.Layout ();
        view.Width = 40;
        view.Layout ();

        Assert.Equal (1, parses);
        Assert.True (GetField<bool> (view, "_parsed"));
        IntermediateBlock block = Assert.Single (GetField<List<IntermediateBlock>> (view, "_blocks"));
        Assert.Equal ("**literal**", Assert.Single (block.Runs).Text);
        Assert.Equal (MarkdownStyleRole.Normal, block.Runs [0].StyleRole);
    }

    [Fact]
    public void Depth_Fallback_Resets_Anchors_And_Slugs_And_Allows_Recovery ()
    {
        using TgMarkdown view = new ()
        {
            MarkdownPipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 16 }.Build (),
            Text = "# Old",
            Width = 60,
            Height = 10
        };
        view.Layout ();
        Assert.True (view.ScrollToAnchor ("old"));
        view.Text = new string ('>', 20);
        view.Layout ();

        Assert.True (GetField<bool> (view, "_parsed"));
        Assert.Empty (GetField<Dictionary<string, int>> (view, "_slugCounts"));
        Assert.Empty (GetField<Dictionary<string, int>> (view, "_headingAnchors"));
        Assert.False (view.ScrollToAnchor ("old"));

        view.Text = "# Old\n\n# Old";
        view.Layout ();

        Assert.True (view.ScrollToAnchor ("old"));
        Assert.True (view.ScrollToAnchor ("old-1"));
        Assert.False (view.ScrollToAnchor ("old-2"));
    }

    [Fact]
    public void ArgumentException_From_Lowering_Is_Not_Swallowed ()
    {
        Mock<ISyntaxHighlighter> highlighter = new ();
        highlighter.Setup (value => value.Highlight (It.IsAny<string> (), It.IsAny<string?> ())).Throws (new ArgumentException ("lowering"));
        using TgMarkdown view = new ()
        {
            SyntaxHighlighter = highlighter.Object,
            Width = 60,
            Height = 10
        };

        ArgumentException exception = Assert.Throws<ArgumentException> (() =>
        {
            view.Text = "```cs\ncode\n```";
            view.Layout ();
        });

        Assert.Equal ("lowering", exception.Message);
    }

    private static T GetField<T> (TgMarkdown view, string name) =>
        (T)typeof (TgMarkdown).GetField (name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue (view)!;
}
