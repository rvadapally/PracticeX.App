using System.Diagnostics;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PracticeX.Application.SourceDiscovery.DocumentAi;
using PracticeX.Discovery.DocumentAi;
using AzureDocPage = Azure.AI.DocumentIntelligence.DocumentPage;
using AzureDocTable = Azure.AI.DocumentIntelligence.DocumentTable;
using AzureDocKvp = Azure.AI.DocumentIntelligence.DocumentKeyValuePair;
using OurDocPage = PracticeX.Discovery.DocumentAi.DocumentPage;
using OurDocTable = PracticeX.Discovery.DocumentAi.DocumentTable;
using OurDocTableCell = PracticeX.Discovery.DocumentAi.DocumentTableCell;
using OurDocKvp = PracticeX.Discovery.DocumentAi.DocumentKeyValuePair;
using OurDocSignature = PracticeX.Discovery.DocumentAi.DocumentSignatureRegion;

namespace PracticeX.Infrastructure.SourceDiscovery.DocumentAi;

/// <summary>
/// Azure Document Intelligence provider. Wraps the v4 SDK
/// (<see cref="DocumentIntelligenceClient"/>). Only registered in DI when
/// <see cref="DocumentIntelligenceOptions.Enabled"/> is true and credentials
/// are populated. Tenant-level allowlist gating happens at the call site, not
/// here — this provider trusts that the caller has already cleared the gate.
/// </summary>
public sealed class AzureDocumentIntelligenceProvider : IDocumentIntelligenceProvider
{
    private readonly DocumentIntelligenceOptions _options;
    private readonly ILogger<AzureDocumentIntelligenceProvider> _logger;
    private readonly DocumentIntelligenceClient _client;

    public AzureDocumentIntelligenceProvider(
        IOptions<DocumentIntelligenceOptions> options,
        ILogger<AzureDocumentIntelligenceProvider> logger)
    {
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException(
                "DocumentIntelligence:Endpoint is required when Enabled=true.");
        }
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "DocumentIntelligence:ApiKey is required when Enabled=true. " +
                "Set via user-secrets — never commit the key to source.");
        }

        _client = new DocumentIntelligenceClient(
            new Uri(_options.Endpoint),
            new AzureKeyCredential(_options.ApiKey));
    }

    public string Name => "azure-document-intelligence";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Endpoint) &&
        !string.IsNullOrWhiteSpace(_options.ApiKey);

    public Task<DocumentExtractionResult> ExtractLayoutAsync(
        DocumentExtractionRequest request,
        CancellationToken cancellationToken) =>
        AnalyzeAsync(request, _options.LayoutModelId, cancellationToken);

    public Task<DocumentExtractionResult> ExtractFieldsAsync(
        DocumentExtractionRequest request,
        string modelId,
        CancellationToken cancellationToken) =>
        AnalyzeAsync(request, modelId, cancellationToken);

    private async Task<DocumentExtractionResult> AnalyzeAsync(
        DocumentExtractionRequest request,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content.Length == 0)
        {
            throw new ArgumentException("Content cannot be empty.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation(
            "DocIntel analyze: model={Model} fileName={FileName} bytes={Bytes}",
            modelId, request.FileName, request.Content.Length);

        var content = BinaryData.FromBytes(request.Content);
        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            modelId,
            content,
            cancellationToken: cancellationToken);

        var result = operation.Value;
        stopwatch.Stop();

        var pages = MapPages(result.Pages);
        var tables = MapTables(result.Tables);
        var kvps = MapKeyValuePairs(result.KeyValuePairs);
        var signatures = ExtractSignatureRegions(result);

        _logger.LogInformation(
            "DocIntel analyze complete: model={Model} pages={Pages} tables={Tables} kvps={Kvps} latencyMs={Latency}",
            modelId, pages.Count, tables.Count, kvps.Count, stopwatch.ElapsedMilliseconds);

        return new DocumentExtractionResult(
            FullText: result.Content ?? string.Empty,
            Pages: pages,
            Tables: tables,
            KeyValuePairs: kvps,
            Signatures: signatures,
            ProviderName: Name,
            ProviderModel: modelId,
            TokensIn: 0,
            TokensOut: 0,
            LatencyMs: stopwatch.ElapsedMilliseconds);
    }

    private static IReadOnlyList<OurDocPage> MapPages(IReadOnlyList<AzureDocPage>? pages)
    {
        if (pages is null || pages.Count == 0) return Array.Empty<OurDocPage>();
        var result = new List<OurDocPage>(pages.Count);
        foreach (var page in pages)
        {
            var pageText = string.Join(
                "\n",
                page.Lines?.Select(l => l.Content) ?? Array.Empty<string>());
            result.Add(new OurDocPage(
                PageNumber: page.PageNumber,
                Text: pageText,
                Width: page.Width ?? 0,
                Height: page.Height ?? 0));
        }
        return result;
    }

    private static IReadOnlyList<OurDocTable> MapTables(IReadOnlyList<AzureDocTable>? tables)
    {
        if (tables is null || tables.Count == 0) return Array.Empty<OurDocTable>();
        var result = new List<OurDocTable>(tables.Count);
        foreach (var table in tables)
        {
            var pageNumber = table.BoundingRegions.Count > 0
                ? table.BoundingRegions[0].PageNumber
                : 0;
            var cells = table.Cells.Select(c => new OurDocTableCell(
                RowIndex: c.RowIndex,
                ColumnIndex: c.ColumnIndex,
                Text: c.Content ?? string.Empty,
                Confidence: 1.0)).ToList();
            result.Add(new OurDocTable(
                PageNumber: pageNumber,
                RowCount: table.RowCount,
                ColumnCount: table.ColumnCount,
                Cells: cells));
        }
        return result;
    }

    private static IReadOnlyList<OurDocKvp> MapKeyValuePairs(IReadOnlyList<AzureDocKvp>? kvps)
    {
        if (kvps is null || kvps.Count == 0) return Array.Empty<OurDocKvp>();
        var result = new List<OurDocKvp>(kvps.Count);
        foreach (var kvp in kvps)
        {
            var pageNumber = kvp.Key?.BoundingRegions?.Count > 0
                ? (int?)kvp.Key.BoundingRegions[0].PageNumber
                : null;
            result.Add(new OurDocKvp(
                Key: kvp.Key?.Content ?? string.Empty,
                Value: kvp.Value?.Content ?? string.Empty,
                PageNumber: pageNumber,
                Confidence: kvp.Confidence));
        }
        return result;
    }

    private static IReadOnlyList<OurDocSignature> ExtractSignatureRegions(AnalyzeResult result)
    {
        // v1 stub: signature regions need either a custom-trained model or
        // selection-mark heuristics. For prebuilt-layout, no native signature
        // detection. CompositeSignatureDetector remains the source of truth
        // for sign/unsigned classification — this seam is reserved for when
        // we enable a custom contract model that emits signature spans.
        return Array.Empty<OurDocSignature>();
    }
}
