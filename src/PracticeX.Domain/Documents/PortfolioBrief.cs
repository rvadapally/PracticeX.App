namespace PracticeX.Domain.Documents;

/// <summary>
/// Slice 16.6 — Practice Intelligence Brief synthesized across every
/// per-document brief by stage 3. Composite-keyed on (TenantId, FacilityId)
/// so each facility owns its own brief; the sentinel
/// 00000000-0000-0000-0000-000000000000 represents the tenant-wide
/// "all facilities" rollup that pre-dates the per-facility split.
/// </summary>
public sealed class PortfolioBrief
{
    /// <summary>The "all facilities" sentinel used when the brief covers
    /// every facility in the tenant rather than a single one.</summary>
    public static readonly Guid AllFacilities = Guid.Empty;

    public Guid TenantId { get; set; }
    public Guid FacilityId { get; set; } = AllFacilities;
    public string BriefMd { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public int SourceDocCount { get; set; }
    public int? LatencyMs { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
