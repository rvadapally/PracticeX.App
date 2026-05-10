using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Domain.Documents;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;

namespace PracticeX.Api.Analysis;

/// <summary>
/// Slice 19 - Renewal Engine. Reads the canonical-headline JSON written by
/// the stage-2 LLM extractor (Slice 18) and projects every doc's term, notice,
/// and confidentiality-survival dates into a single time-bucketed action list.
/// Pure compute on read - no LLM call, no extra persistence; the source of
/// truth is `document_assets.llm_extracted_fields_json["headline"]`.
/// </summary>
public static class RenewalsEndpoint
{
    public static IEndpointRouteBuilder MapRenewalsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapGet("/renewals", GetRenewals).WithName("GetRenewals");
        return routes;
    }

    private static async Task<Ok<RenewalsResponse>> GetRenewals(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var tenantId = userContext.TenantId;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Slice 21 RBAC: renewals reflect facility scope.
        var visibleAssetIds = db.DocumentCandidates
            .Where(c => c.TenantId == tenantId)
            .ApplyFacilityScope(userContext)
            .Select(c => c.DocumentAssetId);
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == tenantId && a.LlmExtractedFieldsJson != null
                     && visibleAssetIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var candidatesByAsset = await db.DocumentCandidates
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.DocumentAssetId, c => c.CandidateType, cancellationToken);

        var actions = new List<RenewalAction>();

        foreach (var asset in assets)
        {
            var fileName = (asset.SourceObjectId.HasValue && sourceNames.TryGetValue(asset.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";
            var candidateType = candidatesByAsset.GetValueOrDefault(asset.Id, "unknown");
            var family = MapFamily(candidateType);

            JsonElement headline;
            try
            {
                using var doc = JsonDocument.Parse(asset.LlmExtractedFieldsJson!);
                if (!doc.RootElement.TryGetProperty("headline", out var hl) ||
                    hl.ValueKind != JsonValueKind.Object)
                    continue;
                headline = hl.Clone();
            }
            catch { continue; }

            // Pull common date/term fields. Many are family-specific but the
            // names are stable across our prompt set (Slice 18).
            var expiration = ReadDate(headline, "expiration_date");
            var commencement = ReadDate(headline, "commencement_date") ?? ReadDate(headline, "effective_date");
            var initialTermMonths = ReadInt(headline, "initial_term_months") ?? ReadInt(headline, "term_months");
            var noticeDays = ReadInt(headline, "without_cause_notice_days");
            var discussionTermMonths = ReadInt(headline, "discussion_term_months");
            var survivalMonths = ReadInt(headline, "confidentiality_survival_months");
            var counterparty = ReadCounterparty(headline, family);

            // Derive expiration when only commencement+term given. Lease
            // amendments often re-state both.
            if (expiration is null && commencement is { } start && initialTermMonths is { } months && months > 0)
            {
                expiration = start.AddMonths(months);
            }

            // Family-specific actions.
            switch (family)
            {
                case "lease":
                    if (expiration is { } leaseExp)
                    {
                        actions.Add(BuildAction(asset.Id, fileName, family, counterparty, "Lease expiration",
                            "Term ends - re-negotiate or vacate decision needed before this date.",
                            leaseExp, today, severity: SeverityForBucket(leaseExp, today)));
                        if (noticeDays is { } nd && nd > 0)
                        {
                            var deadline = leaseExp.AddDays(-nd);
                            actions.Add(BuildAction(asset.Id, fileName, family, counterparty,
                                $"Lease notice deadline ({nd}d before)",
                                "Last day to give written notice if not renewing.",
                                deadline, today, severity: SeverityForBucket(deadline, today)));
                        }
                    }
                    break;

                case "employment_governance":
                case "scheduling":
                    // Initial term ends at commencement + term months.
                    if (expiration is { } empExp)
                    {
                        var label = family == "scheduling" ? "Call coverage initial term ends" : "Employment initial term ends";
                        actions.Add(BuildAction(asset.Id, fileName, family, counterparty, label,
                            "Initial term concludes - verify auto-renewal vs. negotiated extension.",
                            empExp, today, severity: SeverityForBucket(empExp, today)));
                    }
                    if (noticeDays is { } eNotice && eNotice > 0 && expiration is { } eExp)
                    {
                        var deadline = eExp.AddDays(-eNotice);
                        actions.Add(BuildAction(asset.Id, fileName, family, counterparty,
                            $"Without-cause notice deadline ({eNotice}d before)",
                            "Last day to give without-cause termination notice.",
                            deadline, today, severity: SeverityForBucket(deadline, today)));
                    }
                    break;

                case "nda":
                    if (commencement is { } ndaStart)
                    {
                        if (discussionTermMonths is { } disc && disc > 0)
                        {
                            var discEnd = ndaStart.AddMonths(disc);
                            actions.Add(BuildAction(asset.Id, fileName, family, counterparty,
                                "NDA discussion period ends",
                                "Underlying transaction window closes - confirm whether discussions are still active.",
                                discEnd, today, severity: SeverityForBucket(discEnd, today)));
                        }
                        if (survivalMonths is { } surv && surv > 0)
                        {
                            var survEnd = ndaStart.AddMonths(surv);
                            actions.Add(BuildAction(asset.Id, fileName, family, counterparty,
                                "NDA confidentiality survival ends",
                                "Information ceases to be contractually confidential after this date.",
                                survEnd, today, severity: SeverityForBucket(survEnd, today)));
                        }
                    }
                    break;
            }
        }

        // Sort: nearest first; overdue floats to top.
        actions = actions.OrderBy(a => a.ActionDate).ToList();

        var buckets = BuildBuckets(actions);
        var counts = new RenewalCounts(
            Overdue: actions.Count(a => a.DaysFromToday < 0),
            Within30: actions.Count(a => a.DaysFromToday >= 0 && a.DaysFromToday <= 30),
            Within90: actions.Count(a => a.DaysFromToday >= 0 && a.DaysFromToday <= 90),
            Within180: actions.Count(a => a.DaysFromToday >= 0 && a.DaysFromToday <= 180),
            Total: actions.Count);

        return TypedResults.Ok(new RenewalsResponse(
            GeneratedAt: DateTimeOffset.UtcNow,
            Today: today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Counts: counts,
            Buckets: buckets,
            Actions: actions));
    }

    private static IReadOnlyList<RenewalBucket> BuildBuckets(IReadOnlyList<RenewalAction> actions)
    {
        // 6 buckets: overdue, 0-30, 31-90, 91-180, 181-365, >365.
        var bucketDefs = new (string key, string label, Func<int, bool> match)[]
        {
            ("overdue",   "Overdue",          d => d < 0),
            ("d0_30",     "Next 30 days",     d => d >= 0   && d <= 30),
            ("d31_90",    "31–90 days",       d => d >= 31  && d <= 90),
            ("d91_180",   "91–180 days",      d => d >= 91  && d <= 180),
            ("d181_365",  "181–365 days",     d => d >= 181 && d <= 365),
            ("d365_plus", "Beyond 1 year",    d => d > 365),
        };

        return bucketDefs.Select(b => new RenewalBucket(
            Key: b.key,
            Label: b.label,
            Items: actions.Where(a => b.match(a.DaysFromToday)).ToList()
        )).ToList();
    }

    private static RenewalAction BuildAction(
        Guid documentAssetId,
        string fileName,
        string family,
        string? counterparty,
        string actionType,
        string description,
        DateOnly actionDate,
        DateOnly today,
        string severity)
    {
        var days = (actionDate.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue)).Days;
        return new RenewalAction(
            DocumentAssetId: documentAssetId,
            FileName: fileName,
            Family: family,
            Counterparty: counterparty,
            ActionType: actionType,
            Description: description,
            ActionDate: actionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DaysFromToday: days,
            Severity: severity);
    }

    private static string SeverityForBucket(DateOnly action, DateOnly today)
    {
        var days = (action.ToDateTime(TimeOnly.MinValue) - today.ToDateTime(TimeOnly.MinValue)).Days;
        if (days < 0) return "overdue";
        if (days <= 30) return "high";
        if (days <= 90) return "medium";
        if (days <= 180) return "low";
        return "info";
    }

    private static DateOnly? ReadDate(JsonElement headline, string key)
    {
        if (!headline.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Prompts are instructed to use ISO yyyy-MM-dd. Fall back to DateTime
        // parsing if the LLM produced a different shape.
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return DateOnly.FromDateTime(dt);
        return null;
    }

    private static int? ReadInt(JsonElement headline, string key)
    {
        if (!headline.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return null;
    }

    private static string? ReadString(JsonElement headline, string key)
    {
        if (!headline.TryGetProperty(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static string? ReadCounterparty(JsonElement headline, string family)
    {
        // Pick the most "counterpartyish" headline field per family. We deliberately
        // do NOT show the tenant's own name (Eagle GI) - too noisy.
        return family switch
        {
            "lease" => ReadString(headline, "landlord"),
            "nda" => ReadString(headline, "counterparty_name"),
            "employment_governance" => ReadString(headline, "physician_name") ?? ReadString(headline, "employer"),
            "scheduling" => ReadString(headline, "covered_facility") ?? ReadString(headline, "covering_group"),
            _ => null
        };
    }

    private static string MapFamily(string candidateType) => candidateType switch
    {
        DocumentCandidateTypes.Lease or
        DocumentCandidateTypes.LeaseAmendment or
        DocumentCandidateTypes.LeaseLoi => "lease",

        DocumentCandidateTypes.EmployeeAgreement or
        DocumentCandidateTypes.Amendment => "employment_governance",

        DocumentCandidateTypes.Nda => "nda",
        DocumentCandidateTypes.CallCoverageAgreement => "scheduling",

        DocumentCandidateTypes.ServiceAgreement or
        DocumentCandidateTypes.VendorContract => "vendor_services",

        _ => "other"
    };
}

// ----------------------------------------------------------------------------
// DTOs
// ----------------------------------------------------------------------------

public sealed record RenewalsResponse(
    DateTimeOffset GeneratedAt,
    string Today,
    RenewalCounts Counts,
    IReadOnlyList<RenewalBucket> Buckets,
    IReadOnlyList<RenewalAction> Actions);

public sealed record RenewalCounts(int Overdue, int Within30, int Within90, int Within180, int Total);

public sealed record RenewalBucket(string Key, string Label, IReadOnlyList<RenewalAction> Items);

public sealed record RenewalAction(
    Guid DocumentAssetId,
    string FileName,
    string Family,
    string? Counterparty,
    string ActionType,
    string Description,
    string ActionDate,
    int DaysFromToday,
    string Severity);
