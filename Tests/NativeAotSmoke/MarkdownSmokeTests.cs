using System.Text.RegularExpressions;
using Markdig;
using Markdown = Terminal.Gui.Views.Markdown;

namespace NativeAotSmoke;

// CoPilot - GPT-6
internal static class MarkdownSmokeTests
{
    public static void Run ()
    {
        using Markdown view = new ()
        {
            Text = "first  \nsecond\n---",
            Width = 60,
            Height = 20,
            ShowCopyButtons = false
        };
        view.Layout ();

        if (view.LineCount != 2 || !view.ScrollToAnchor ("first-second"))
        {
            throw new InvalidOperationException ("AOT Markdown heading layout or anchor generation failed.");
        }

        AssertRendered (view, "Term **bold**\n: definition", "Term bold\n  definition");
        AssertRendered (view, "paragraph\n\n-", "paragraph\n\n•");
        AssertRendered (view, "- first  \n  second", "• first\n  second");
        AssertRendered (view, "*[HTML]: HyperText Markup Language\n\nHTML supports **bold**", "HTML supports bold");
        AssertRendered (view, "[reference][target]\n\n[target]: https://example.com", "reference");
        AssertRendered (view, "```csharp\nint answer = 42;\n```", "int answer = 42;");

        view.MarkdownPipeline = new MarkdownPipelineBuilder ().UseAdvancedExtensions ().UseEmojiAndSmiley ().UseCjkFriendlyEmphasis ().Build ();
        string table = view.RenderToAnsi ("| Name | Value |\n| --- | --- |\n| cell | :x: |");

        if (!table.Contains ("Name", StringComparison.Ordinal) || !table.Contains ("❌", StringComparison.Ordinal))
        {
            throw new InvalidOperationException ("AOT Markdown table rendering failed.");
        }
    }

    private static void AssertRendered (Markdown view, string markdown, string expected)
    {
        string ansi = view.RenderToAnsi (markdown);
        string plain = Regex.Replace (ansi, @"\x1b\[[0-?]*[ -/]*[@-~]", string.Empty);
        plain = Regex.Replace (plain, @"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", string.Empty);
        string normalized = string.Join ("\n", plain.Replace ("\r\n", "\n").TrimEnd ().Split ('\n').Select (line => line.TrimEnd ()));

        if (normalized != expected)
        {
            throw new InvalidOperationException ($"AOT Markdown rendering failed: expected '{expected}', got '{normalized}'.");
        }
    }
}
