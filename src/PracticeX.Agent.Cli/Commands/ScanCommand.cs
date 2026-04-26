using PracticeX.Agent.Cli.Http;
using PracticeX.Agent.Cli.Inventory;
using PracticeX.Agent.Cli.Output;

namespace PracticeX.Agent.Cli.Commands;

/// <summary>
/// End-to-end scan: enumerate folder → POST manifest → render report → upload
/// selected files as a bundle. Returns the process exit code per the spec
/// (0 ok, 1 partial, 2 transport/config error).
/// </summary>
public static class ScanCommand
{
    public static async Task<int> RunAsync(ScanOptions options, TextWriter @out, TextWriter err, CancellationToken cancellationToken)
    {
        @out.WriteLine($"Inventorying {options.Root} ...");
        List<ManifestItemDto> items;
        try
        {
            items = FolderEnumerator.Enumerate(options.Root).ToList();
        }
        catch (DirectoryNotFoundException ex)
        {
            err.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (items.Count == 0)
        {
            err.WriteLine("No files passed inventory filters. Nothing to score.");
            return 0;
        }

        @out.WriteLine($"  Inventoried {items.Count} files. Posting metadata-only manifest...");

        using var client = new PracticeXClient(options.ApiBaseUrl, options.ConnectionId, options.Token, options.Insecure);

        ManifestScanResponse manifest;
        try
        {
            manifest = await client.PostManifestAsync(items, options.Notes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            err.WriteLine($"error: manifest scan failed: {ex.Message}");
            return 2;
        }

        var selection = ScanReport.PartitionForUpload(manifest, options.AutoSelectBands);
        ScanReport.PrintScanSummary(@out, options.Root, manifest, selection, options.AutoSelectBands);

        if (options.DryRun)
        {
            @out.WriteLine();
            @out.WriteLine("--dry-run set: skipping bundle upload.");
            return 0;
        }

        if (selection.Selected.Count == 0)
        {
            @out.WriteLine();
            @out.WriteLine("Nothing selected for upload. Exiting.");
            return 0;
        }

        var bundleFiles = MapBundleFiles(options.Root, selection.Selected, items);

        @out.WriteLine();
        @out.WriteLine($"Uploading bundle to /folder/bundles?batchId={manifest.BatchId} ...");
        IngestionBatchSummaryDto summary;
        try
        {
            summary = await client.PostBundleAsync(manifest.BatchId, bundleFiles, options.Notes, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            err.WriteLine($"error: bundle upload failed: {ex.Message}");
            return 2;
        }

        ScanReport.PrintBundleSummary(@out, summary);

        return summary.ErrorCount > 0 ? 1 : 0;
    }

    private static List<BundleFile> MapBundleFiles(
        string root,
        IReadOnlyList<ManifestScoredItemDto> selected,
        IReadOnlyList<ManifestItemDto> manifestItems)
    {
        // Original manifest carries the mime guess we sent; preserve it on
        // upload so the cloud stores the same Content-Type it scored.
        var mimeByPath = manifestItems
            .GroupBy(i => i.RelativePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().MimeType ?? "application/octet-stream", StringComparer.Ordinal);

        var bundle = new List<BundleFile>(selected.Count);
        foreach (var s in selected)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, s.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var mime = mimeByPath.TryGetValue(s.RelativePath, out var m) ? m : "application/octet-stream";
            bundle.Add(new BundleFile(
                AbsolutePath: absolute,
                RelativePath: s.RelativePath,
                Name: s.Name,
                MimeType: mime,
                ManifestItemId: s.ManifestItemId));
        }
        return bundle;
    }
}
