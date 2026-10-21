using UnitTests;

namespace ViewsTests.Markdown;

// CoPilot - GPT-6
public class MarkdownTableLayoutRegressionTests : TestDriverBase
{
    [Theory]
    [InlineData ("\t")]
    [InlineData ("\n")]
    public void Wrapping_Uses_The_Same_Word_Boundaries_As_Measurement (string separator)
    {
        List<StyledSegment> segments = [new ($"a{separator}bbbbbbbb", MarkdownStyleRole.Normal)];
        int wordWidth = MarkdownTable.MeasureLongestWord (segments);
        List<List<StyledSegment>> lines = MarkdownTable.WrapSegments (segments, wordWidth);

        Assert.Equal (8, wordWidth);
        Assert.Equal (["a", "bbbbbbbb"], lines.Select (line => string.Concat (line.Select (segment => segment.Text)).TrimEnd ()));
    }

    [Fact]
    public void Resizing_Preserves_Cached_Width_Bounds_Across_Style_Boundaries ()
    {
        MarkdownTableContent content = new ([[new ("H", MarkdownStyleRole.Heading)]],
                                           [[[new ("long word", MarkdownStyleRole.Normal)]],
                                            [[new ("abc", MarkdownStyleRole.Normal), new ("def", MarkdownStyleRole.Strong)]]]);

        Assert.Equal ([11], MarkdownTable.ComputeColumnWidths (content, 80));
        Assert.Equal ([8], MarkdownTable.ComputeColumnWidths (content, 10));
        Assert.Equal ([11], MarkdownTable.ComputeColumnWidths (content, 80));
        Assert.Equal ([9], content.MaximumWidths);
        Assert.Equal ([6], content.MinimumWidths);
    }

    [Fact]
    public void Tab_Separated_Cell_Draws_The_Long_Word_Without_Splitting_It ()
    {
        using IDriver driver = CreateTestDriver (12, 10);
        using MarkdownTable table = new ()
        {
            Driver = driver,
            TableData = new (["H"], [Alignment.Start], [["a\tbbbbbbbb"]]),
            Width = 12
        };
        table.Layout ();
        table.Draw ();

        Assert.Equal ("a", ReadText (3).TrimEnd ());
        Assert.Equal ("bbbbbbbb", ReadText (4));

        return;

        string ReadText (int row) => string.Concat (Enumerable.Range (2, 8).Select (column => driver.Contents! [row, column].Grapheme));
    }
}
