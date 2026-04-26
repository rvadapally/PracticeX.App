using PracticeX.Application.Common;
using PracticeX.Application.SourceDiscovery.Complexity;
using PracticeX.Application.SourceDiscovery.Connectors;

namespace PracticeX.Application.SourceDiscovery.Ingestion;

/// <summary>
/// Owns the canonical ingestion pipeline:
///   discovered_items -> source_objects -> ingestion_batch + jobs
///   -> document_assets -> document_candidates -> review_tasks -> audit
///
/// Connectors only produce DiscoveredItems. They never write canonical contract
/// records. Approval into the contract repository happens in the review queue.
/// </summary>
public interface IIngestionOrchestrator
{
    Task<Result<IngestionBatchSummary>> IngestAsync(
        IngestionRequest request,
        DiscoveryResult discovery,
        CancellationToken cancellationToken);

    /// <summary>
    /// Scores a folder manifest — metadata only, no bytes uploaded — and persists
    /// the result as a phase='manifest' ingestion batch with proposed source_objects.
    /// The same batch is later promoted by IngestBundleAsync once the user has
    /// chosen which files to actually upload.
    /// </summary>
    Task<Result<ManifestScanResult>> ScoreManifestAsync(
        IngestionRequest request,
        IReadOnlyList<ManifestItem> items,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hydrates a previously-scored manifest batch with the bytes for the
    /// selected items, creating document_assets, candidates, and review tasks.
    /// </summary>
    Task<Result<IngestionBatchSummary>> IngestBundleAsync(
        IngestionRequest request,
        Guid manifestBatchId,
        DiscoveryResult discovery,
        CancellationToken cancellationToken);
}

public sealed class IngestionRequest
{
    public required Guid TenantId { get; init; }
    public required Guid InitiatedByUserId { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string SourceType { get; init; }
    public string? Notes { get; init; }
}

public sealed class IngestionBatchSummary
{
    public required Guid BatchId { get; init; }
    public required int FileCount { get; init; }
    public required int CandidateCount { get; init; }
    public required int SkippedCount { get; init; }
    public required int ErrorCount { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<IngestionItemSummary> Items { get; init; }
    public BatchComplexityProfile? Complexity { get; init; }
}

public sealed class IngestionItemSummary
{
    public required Guid SourceObjectId { get; init; }
    public Guid? DocumentAssetId { get; init; }
    public Guid? DocumentCandidateId { get; init; }
    public required string Name { get; init; }
    public required string CandidateType { get; init; }
    public decimal Confidence { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public required string Status { get; init; }
    public string? RelativePath { get; init; }
    public string? ComplexityTier { get; init; }
    public IReadOnlyList<string>? ComplexityFactors { get; init; }
    public IReadOnlyList<string>? ComplexityBlockers { get; init; }
    public decimal? EstimatedComplexityHours { get; init; }
}

public sealed class ManifestItem
{
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastModifiedUtc { get; init; }
    public string? MimeType { get; init; }
}

public sealed class ManifestScanResult
{
    public required Guid BatchId { get; init; }
    public required string Phase { get; init; }
    public required IReadOnlyList<ManifestScoredItem> Items { get; init; }
}

public sealed class ManifestScoredItem
{
    public required string ManifestItemId { get; init; }
    public required string RelativePath { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    public required string CandidateType { get; init; }
    public required decimal Confidence { get; init; }
    public required IReadOnlyList<string> ReasonCodes { get; init; }
    public required string RecommendedAction { get; init; }
    public required string Band { get; init; }
    public string? CounterpartyHint { get; init; }
}

// ManifestBands and ManifestRecommendedActions live in
// PracticeX.Discovery.Contracts so the desktop agent and any external consumer
// can reference them without pulling the Application assembly. Keep imports
// directly there: `using PracticeX.Discovery.Contracts;`.
