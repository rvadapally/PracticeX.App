namespace PracticeX.Domain.Documents;

/// <summary>
/// Slice 20 — per-tenant Counsel's Brief synthesized across every per-document
/// Counsel's Memo. Posture is adversarial corporate-counsel (top risks,
/// conflicts, missing protections) — distinct from PortfolioBrief, which is
/// the practice-owner / partner executive view. One row per tenant.
/// </summary>
public sealed class CounselBrief
{
    public Guid TenantId { get; set; }
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
