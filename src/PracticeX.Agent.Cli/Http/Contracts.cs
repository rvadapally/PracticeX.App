using System.Text.Json.Serialization;

namespace PracticeX.Agent.Cli.Http;

// Mirrors PracticeX.Api.SourceDiscovery DTOs. Copied (not referenced) so the CLI
// stays a pure HTTP client that only depends on the wire contract, not the API
// project. Drift is caught by integration tests.

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

public sealed record ProblemDetailsDto(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail
);

public static class ManifestBandNames
{
    public const string Strong = "strong";
    public const string Likely = "likely";
    public const string Possible = "possible";
    public const string Skipped = "skipped";
}
