namespace PracticeX.Domain.Documents;

/// <summary>
/// Slice 16.6 — per-tenant Practice Intelligence Brief synthesized across
/// every per-document brief by stage 3. One row per tenant; the latest
/// generation wins on regenerate.
/// </summary>
public sealed class PortfolioBrief
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
