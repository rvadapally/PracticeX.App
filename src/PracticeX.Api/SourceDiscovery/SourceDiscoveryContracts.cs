namespace PracticeX.Api.SourceDiscovery;

public sealed record ConnectorDescriptorDto(
    string SourceType,
    string DisplayName,
    string Summary,
    string AuthMode,
    bool IsReadOnly,
    string Status,
    IReadOnlyCollection<string> SupportedMimeTypes
);

public sealed record SourceConnectionDto(
    Guid Id,
    string SourceType,
    string Status,
    string? DisplayName,
    string? OauthSubject,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset CreatedAt,
    string? LastError
);

public sealed record CreateConnectionRequest(string SourceType, string? DisplayName);

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

public sealed record IngestionItemDto(
    Guid SourceObjectId,
    Guid? DocumentAssetId,
    Guid? DocumentCandidateId,
    string Name,
    string CandidateType,
    decimal Confidence,
    IReadOnlyList<string> ReasonCodes,
    string Status,
    string? RelativePath
);

public sealed record IngestionBatchSummaryDto(
    Guid BatchId,
    int FileCount,
    int CandidateCount,
    int SkippedCount,
    int ErrorCount,
    string Status,
    IReadOnlyList<IngestionItemDto> Items
);

public sealed record OutlookOAuthStartResponse(string AuthorizeUrl, string State);

public sealed record DeleteAllBatchesResult(int DeletedCount);

public sealed record OutlookScanRequest(int? Top, DateTimeOffset? Since);

public sealed record ManifestItemDto(
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string? MimeType
);

public sealed record ManifestScanRequest(
    IReadOnlyList<ManifestItemDto> Items,
    string? Notes
);

public sealed record ManifestScoredItemDto(
    string ManifestItemId,
    string RelativePath,
    string Name,
    long SizeBytes,
    string CandidateType,
    decimal Confidence,
    IReadOnlyList<string> ReasonCodes,
    string RecommendedAction,
    string Band,
    string? CounterpartyHint
);

public sealed record ManifestScanResponse(
    Guid BatchId,
    string Phase,
    int TotalItems,
    int StrongCount,
    int LikelyCount,
    int PossibleCount,
    int SkippedCount,
    IReadOnlyList<ManifestScoredItemDto> Items
);
