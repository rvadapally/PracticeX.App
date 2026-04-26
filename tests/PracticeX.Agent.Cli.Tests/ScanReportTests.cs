using PracticeX.Agent.Cli.Http;
using PracticeX.Agent.Cli.Output;

namespace PracticeX.Agent.Cli.Tests;

public class ScanReportTests
{
    [Fact]
    public void PartitionForUpload_DefaultBands_PicksStrongAndLikely()
    {
        var response = ResponseWith(
            Item("a.pdf", ManifestBandNames.Strong),
            Item("b.pdf", ManifestBandNames.Likely),
            Item("c.pdf", ManifestBandNames.Possible),
            Item("d.png", ManifestBandNames.Skipped));
        var bands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "strong", "likely" };

        var selection = ScanReport.PartitionForUpload(response, bands);

        Assert.Equal(2, selection.Selected.Count);
        Assert.Contains(selection.Selected, i => i.Name == "a.pdf");
        Assert.Contains(selection.Selected, i => i.Name == "b.pdf");
        Assert.Equal(2, selection.Skipped.Count);
    }

    [Fact]
    public void PartitionForUpload_PossibleBand_OptIn()
    {
        var response = ResponseWith(
            Item("a.pdf", ManifestBandNames.Strong),
            Item("c.pdf", ManifestBandNames.Possible));
        var bands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "strong", "likely", "possible" };

        var selection = ScanReport.PartitionForUpload(response, bands);

        Assert.Equal(2, selection.Selected.Count);
    }

    [Fact]
    public void PartitionForUpload_SkippedBand_NeverSelected_EvenIfRequested()
    {
        var response = ResponseWith(
            Item("a.pdf", ManifestBandNames.Strong),
            Item("d.png", ManifestBandNames.Skipped));
        // Operator tries to be cheeky and asks for skipped too.
        var bands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "strong", "skipped" };

        var selection = ScanReport.PartitionForUpload(response, bands);

        Assert.Single(selection.Selected);
        Assert.Equal("a.pdf", selection.Selected[0].Name);
    }

    [Fact]
    public void PrintScanSummary_IncludesBatchIdAndCounts()
    {
        var response = ResponseWith(
            Item("a.pdf", ManifestBandNames.Strong),
            Item("b.pdf", ManifestBandNames.Likely),
            Item("c.pdf", ManifestBandNames.Possible),
            Item("d.png", ManifestBandNames.Skipped));
        var bands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "strong", "likely" };
        var selection = ScanReport.PartitionForUpload(response, bands);

        using var writer = new StringWriter();
        ScanReport.PrintScanSummary(writer, "C:\\Test", response, selection, bands);
        var output = writer.ToString();

        Assert.Contains("Scanned 4 files", output);
        Assert.Contains("Strong", output);
        Assert.Contains("Likely", output);
        Assert.Contains("Possible", output);
        Assert.Contains("Skipped", output);
        Assert.Contains(response.BatchId.ToString(), output);
        Assert.Contains("Selecting 2 files for upload, avoiding 2", output);
    }

    private static ManifestScoredItemDto Item(string name, string band) => new(
        ManifestItemId: $"manifest:{name}|0|0",
        RelativePath: name,
        Name: name,
        SizeBytes: 1024,
        CandidateType: "contract",
        Confidence: band switch
        {
            ManifestBandNames.Strong => 0.9m,
            ManifestBandNames.Likely => 0.7m,
            ManifestBandNames.Possible => 0.5m,
            _ => 0.1m
        },
        ReasonCodes: ["filename_keyword"],
        RecommendedAction: "select",
        Band: band,
        CounterpartyHint: null);

    private static ManifestScanResponse ResponseWith(params ManifestScoredItemDto[] items) => new(
        BatchId: Guid.NewGuid(),
        Phase: "manifest",
        TotalItems: items.Length,
        StrongCount: items.Count(i => i.Band == ManifestBandNames.Strong),
        LikelyCount: items.Count(i => i.Band == ManifestBandNames.Likely),
        PossibleCount: items.Count(i => i.Band == ManifestBandNames.Possible),
        SkippedCount: items.Count(i => i.Band == ManifestBandNames.Skipped),
        Items: items);
}
