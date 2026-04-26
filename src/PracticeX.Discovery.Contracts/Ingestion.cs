namespace PracticeX.Discovery.Contracts;

public sealed record IngestionItemDto(
    Guid SourceObjectId,
    Guid? DocumentAssetId,
    Guid? DocumentCandidateId,
    string Name,
    string CandidateType,
    decimal Confidence,
    IReadOnlyList<string> ReasonCodes,
    string Status,
    string? RelativePath,
    // Complexity profile — populated for items whose bytes were ingested
    // (manifest-only items have these null since complexity needs the file).
    string? ComplexityTier = null,
    IReadOnlyList<string>? ComplexityFactors = null,
    IReadOnlyList<string>? ComplexityBlockers = null,
    decimal? EstimatedComplexityHours = null
);

/// <summary>
/// Per-batch complexity aggregate — drives the "you're about to send N files
/// at $X estimated cost" preview and the post-upload summary in the UI.
/// </summary>
public sealed record BatchComplexityProfileDto(
    int SimpleCount,
    int ModerateCount,
    int LargeCount,
    int ExtraCount,
    decimal? TotalEstimatedHours,
    int? EstimatedDocumentIntelligencePages,
    decimal? EstimatedDocumentIntelligenceCostUsd,
    IReadOnlyList<BlockerSummaryDto> Blockers
);

public sealed record BlockerSummaryDto(string Code, int Count);

public sealed record IngestionBatchSummaryDto(
    Guid BatchId,
    int FileCount,
    int CandidateCount,
    int SkippedCount,
    int ErrorCount,
    string Status,
    IReadOnlyList<IngestionItemDto> Items,
    BatchComplexityProfileDto? Complexity = null
);

public sealed record IngestionBatchDto(
    Guid Id,
    string SourceType,
    Guid? SourceConnectionId,
    string Status,
    int FileCount,
    int CandidateCount,
    int SkippedCount,
    int ErrorCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? Notes
);

public sealed record DocumentCandidateDto(
    Guid Id,
    Guid? SourceObjectId,
    Guid DocumentAssetId,
    string CandidateType,
    decimal Confidence,
    string Status,
    IReadOnlyList<string> ReasonCodes,
    string ClassifierVersion,
    string? OriginFilename,
    string? RelativePath,
    string? CounterpartyHint,
    DateTimeOffset CreatedAt
);

public sealed record DeleteAllBatchesResult(int DeletedCount);
