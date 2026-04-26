using PracticeX.Agent.Cli.Http;

namespace PracticeX.Agent.Cli.Output;

/// <summary>
/// Renders the manifest scan response and bundle summary as a tidy console
/// report. Pure functions on the response shape so the output can be tested
/// without a real cloud.
/// </summary>
public static class ScanReport
{
    public sealed record Selection(
        IReadOnlyList<ManifestScoredItemDto> Selected,
        IReadOnlyList<ManifestScoredItemDto> Skipped,
        int StrongCount,
        int LikelyCount,
        int PossibleCount,
        int SkippedBandCount
    );

    public static Selection PartitionForUpload(
        ManifestScanResponse response,
        IReadOnlySet<string> autoSelectBands)
    {
        var selected = new List<ManifestScoredItemDto>(response.Items.Count);
        var skipped = new List<ManifestScoredItemDto>(response.Items.Count);

        foreach (var item in response.Items)
        {
            // Skipped band is hard-locked: never uploadable, even if the
            // operator passes --auto-select strong,likely,possible,skipped.
            if (item.Band != ManifestBandNames.Skipped && autoSelectBands.Contains(item.Band))
            {
                selected.Add(item);
            }
            else
            {
                skipped.Add(item);
            }
        }

        return new Selection(
            selected,
            skipped,
            response.StrongCount,
            response.LikelyCount,
            response.PossibleCount,
            response.SkippedCount);
    }

    public static void PrintScanSummary(
        TextWriter @out,
        string root,
        ManifestScanResponse response,
        Selection selection,
        IReadOnlySet<string> autoSelectBands)
    {
        @out.WriteLine($"Scanned {response.TotalItems} files in {root}");
        @out.WriteLine($"  Strong   {response.StrongCount,5}  {BandSuffix(ManifestBandNames.Strong, autoSelectBands)}");
        @out.WriteLine($"  Likely   {response.LikelyCount,5}  {BandSuffix(ManifestBandNames.Likely, autoSelectBands)}");
        @out.WriteLine($"  Possible {response.PossibleCount,5}  {BandSuffix(ManifestBandNames.Possible, autoSelectBands)}");
        @out.WriteLine($"  Skipped  {response.SkippedCount,5}  (never uploadable)");
        @out.WriteLine();
        @out.WriteLine($"Selecting {selection.Selected.Count} files for upload, avoiding {response.TotalItems - selection.Selected.Count}.");
        @out.WriteLine($"Manifest batch: {response.BatchId}  (phase={response.Phase})");
    }

    public static void PrintBundleSummary(TextWriter @out, IngestionBatchSummaryDto summary)
    {
        @out.WriteLine();
        @out.WriteLine($"Bundle complete. Batch {summary.BatchId} status={summary.Status}");
        @out.WriteLine($"  Files received  : {summary.FileCount}");
        @out.WriteLine($"  Candidates      : {summary.CandidateCount}");
        @out.WriteLine($"  Skipped (dup)   : {summary.SkippedCount}");
        @out.WriteLine($"  Errors          : {summary.ErrorCount}");
    }

    private static string BandSuffix(string band, IReadOnlySet<string> autoSelectBands)
        => autoSelectBands.Contains(band)
            ? "(auto-selected)"
            : "(not selected — pass --auto-select to include)";
}
