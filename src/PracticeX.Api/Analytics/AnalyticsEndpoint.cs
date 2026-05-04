using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Domain.Audit;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Api.Analytics;

/// <summary>
/// Lightweight UI activity logger. Frontend posts page-view + key-action
/// events here; we land them in <c>audit.audit_events</c> with
/// <c>actor_type='external_viewer'</c> and the authenticated email pulled
/// from Cloudflare Access's <c>Cf-Access-Authenticated-User-Email</c>
/// header. Used post-demo to see which sections a guest spent time on.
///
/// Identity flow: Cloudflare Access OTP-authenticates the user at
/// <c>app.practicex.ai</c> and injects the header on every request. The
/// Pages Function proxy forwards all headers, so it lands here untouched.
/// Anonymous-fallback when the header is missing keeps the endpoint working
/// during local dev (the harek user hits the API directly without Access).
/// </summary>
public static class AnalyticsEndpoint
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analytics").WithTags("Analytics");
        group.MapPost("/event", LogEvent).WithName("LogAnalyticsEvent");
        group.MapGet("/events", ListEvents).WithName("ListAnalyticsEvents");
        return routes;
    }

    private static async Task<IResult> LogEvent(
        [FromBody] AnalyticsEventDto dto,
        HttpContext httpContext,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.EventType) || dto.EventType.Length > 80)
        {
            return Results.BadRequest(new { error = "eventType is required and ≤80 chars" });
        }

        var email = httpContext.Request.Headers["Cf-Access-Authenticated-User-Email"].ToString();
        if (string.IsNullOrWhiteSpace(email))
        {
            email = "(anonymous)";
        }

        // Trim path — guard against unbounded payloads while keeping enough
        // context for the post-demo report.
        var path = (dto.Path ?? "").Length > 512 ? dto.Path![..512] : (dto.Path ?? "");
        var meta = new Dictionary<string, object?>
        {
            ["email"] = email,
            ["path"] = path,
            ["referrer"] = (dto.Referrer ?? "").Length > 512 ? dto.Referrer![..512] : (dto.Referrer ?? ""),
            ["userAgent"] = httpContext.Request.Headers.UserAgent.ToString().Length > 512
                ? httpContext.Request.Headers.UserAgent.ToString()[..512]
                : httpContext.Request.Headers.UserAgent.ToString(),
        };
        if (dto.Metadata is not null)
        {
            foreach (var (k, v) in dto.Metadata)
            {
                if (meta.ContainsKey(k)) continue;       // never let client overwrite path/email
                if (k.Length > 60) continue;
                meta[k] = v;
            }
        }

        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = userContext.TenantId,
            ActorType = "external_viewer",
            ActorId = null,
            EventType = $"ui.{dto.EventType}",
            ResourceType = "ui_event",
            ResourceId = Guid.Empty,
            MetadataJson = JsonSerializer.Serialize(meta),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// Returns recent UI events grouped by email. Use this after the demo
    /// to see what the guest explored. No auth gate beyond the existing
    /// Cloudflare Access cookie; in production this would require an admin
    /// role.
    /// </summary>
    private static async Task<Ok<AnalyticsReport>> ListEvents(
        string? since,
        string? email,
        int? limit,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var sinceDt = DateTimeOffset.TryParse(since, out var s) ? s : DateTimeOffset.UtcNow.AddDays(-7);
        var cap = Math.Clamp(limit ?? 500, 1, 5000);

        var rows = await db.AuditEvents
            .Where(a => a.TenantId == userContext.TenantId
                     && a.ActorType == "external_viewer"
                     && a.CreatedAt >= sinceDt)
            .OrderByDescending(a => a.CreatedAt)
            .Take(cap)
            .ToListAsync(cancellationToken);

        var events = new List<AnalyticsEventRow>(rows.Count);
        foreach (var r in rows)
        {
            string? rowEmail = null, path = null, ua = null;
            Dictionary<string, JsonElement>? extra = null;
            if (!string.IsNullOrEmpty(r.MetadataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.MetadataJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        switch (prop.Name)
                        {
                            case "email": rowEmail = prop.Value.GetString(); break;
                            case "path": path = prop.Value.GetString(); break;
                            case "userAgent": ua = prop.Value.GetString(); break;
                            default:
                                (extra ??= new())[prop.Name] = prop.Value.Clone();
                                break;
                        }
                    }
                }
                catch { /* ignore */ }
            }
            if (!string.IsNullOrWhiteSpace(email) &&
                !string.Equals(rowEmail, email, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            events.Add(new AnalyticsEventRow(
                CreatedAt: r.CreatedAt,
                Email: rowEmail ?? "(unknown)",
                EventType: r.EventType,
                Path: path,
                UserAgent: ua,
                Extra: extra
            ));
        }

        var byEmail = events
            .GroupBy(e => e.Email)
            .Select(g => new EmailSummary(
                Email: g.Key,
                EventCount: g.Count(),
                FirstSeen: g.Min(e => e.CreatedAt),
                LastSeen: g.Max(e => e.CreatedAt),
                DistinctPaths: g.Where(e => !string.IsNullOrEmpty(e.Path))
                    .Select(e => e.Path!).Distinct().Count(),
                TopPaths: g.GroupBy(e => e.Path ?? "")
                    .Select(p => new PathHit(p.Key, p.Count()))
                    .OrderByDescending(p => p.Hits)
                    .Take(8)
                    .ToList()
            ))
            .OrderByDescending(s => s.LastSeen)
            .ToList();

        return TypedResults.Ok(new AnalyticsReport(
            Since: sinceDt,
            TotalEvents: events.Count,
            ByEmail: byEmail,
            Events: events
        ));
    }
}

public sealed record AnalyticsEventDto(
    string EventType,
    string? Path,
    string? Referrer,
    Dictionary<string, JsonElement>? Metadata);

public sealed record AnalyticsEventRow(
    DateTimeOffset CreatedAt,
    string Email,
    string EventType,
    string? Path,
    string? UserAgent,
    Dictionary<string, JsonElement>? Extra);

public sealed record PathHit(string Path, int Hits);

public sealed record EmailSummary(
    string Email,
    int EventCount,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int DistinctPaths,
    IReadOnlyList<PathHit> TopPaths);

public sealed record AnalyticsReport(
    DateTimeOffset Since,
    int TotalEvents,
    IReadOnlyList<EmailSummary> ByEmail,
    IReadOnlyList<AnalyticsEventRow> Events);
