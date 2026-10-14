using System.Text;

namespace DriverTests.Output;

public class InlineOutputLayoutTests
{
    [Theory]
    [InlineData ("", "")]
    [InlineData ("\n", "")]
    [InlineData ("a\n", "a")]
    [InlineData ("a\n\n", "a|")]
    [InlineData ("a\r\nb\rc", "a|b|c")]
    public void StringNewlines (string text, string expected) =>
        Assert.Equal (expected, string.Join ("|", ApplicationInlineOutputExtensions.Split (text).Select (Text)));

    [Theory]
    [InlineData ("abcd", 4, "abcd")]
    [InlineData ("abcde", 4, "abcd|e")]
    [InlineData ("ab界", 3, "ab|界")]
    [InlineData ("界", 1, "�")]
    [InlineData ("a\t", 4, "a   |    ")]
    [InlineData ("é", 1, "é")]
    [InlineData ("👩‍👩‍👧‍👦", 2, "👩‍👩‍👧‍👦")]
    public void WrapsByDisplayCells (string text, int width, string expected)
    {
        InlineOutputLayout layout = new ([new InlineOutputLine ([new InlineOutputSpan (text)])]);
        InlineOutputLine [] rows = layout.Rows (width).ToArray ();
        Assert.Equal (expected, string.Join ("|", rows.Select (Text)));
        Assert.Equal (rows.Length, layout.CountRows (width));
    }

    [Fact]
    public void GraphemesAndSurrogatesAcrossSpansUseFirstStyle ()
    {
        Attribute red = new (Color.Red, Color.None);
        InlineOutputLayout layout = new ([new InlineOutputLine ([new InlineOutputSpan ("e", red), new InlineOutputSpan ("́"),
                                                                new InlineOutputSpan ("\ud83d", red), new InlineOutputSpan ("\ude00")])]);
        InlineOutputLine [] rows = layout.Rows (2).ToArray ();
        Assert.Equal (["é", "😀"], rows.Select (Text));
        Assert.All (rows.SelectMany (row => row.Spans), span => Assert.Equal (red, span.Attribute));
    }

    [Theory]
    [InlineData ("\u001b[2J")]
    [InlineData ("a\n")]
    [InlineData ("\r")]
    [InlineData ("\0")]
    [InlineData ("\u007f")]
    [InlineData ("\u0085")]
    public void RejectsControlsAndInvalidUnicode (string text) =>
        Assert.Throws<ArgumentException> (() => new InlineOutputLayout ([new InlineOutputLine ([new InlineOutputSpan (text)])]));

    [Fact]
    public void InvalidSurrogatesAreRejectedWithoutTestDataSerialization ()
    {
        foreach (char surrogate in (char [])['\ud800', '\udc00'])
        {
            string text = new (surrogate, 1);
            Assert.Throws<ArgumentException> (() => new InlineOutputLayout ([new InlineOutputLine ([new InlineOutputSpan (text)])]));
        }
    }

    [Fact]
    public void SnapshotsAndRejectsNullsAndOversizedPayloads ()
    {
        List<InlineOutputSpan> spans = [new ("original")];
        InlineOutputLine line = new (spans);
        List<InlineOutputLine> lines = [line];
        InlineOutputLayout layout = new (lines);
        spans.Clear ();
        lines.Clear ();
        Assert.Equal ("original", Text (Assert.Single (layout.Rows (80))));
        Assert.Throws<ArgumentNullException> (() => new InlineOutputLayout ([null!]));
        Assert.Throws<ArgumentNullException> (() => new InlineOutputLayout ([new InlineOutputLine ([null!])]));
        Assert.Throws<ArgumentNullException> (() => new InlineOutputLayout ([new InlineOutputLine ([new InlineOutputSpan (null!)])]));
        Assert.Throws<InvalidOperationException> (() => new InlineOutputLayout ([new InlineOutputLine ([new InlineOutputSpan (new string ('x', InlineOutputLayout.MaximumUnits))])]));
        Assert.True (new InlineOutputLayout ([]).IsEmpty);
        Assert.Throws<ArgumentOutOfRangeException> (() => layout.CountRows (0));
    }

    private static string Text (InlineOutputLine line) => string.Concat (line.Spans.Select (span => span.Text));
}
