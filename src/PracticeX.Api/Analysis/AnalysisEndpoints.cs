using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Application.SourceDiscovery.Storage;
using PracticeX.Domain.Documents;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.Tenancy;

namespace PracticeX.Api.Analysis;

/// <summary>
/// "Premium analysis surface" endpoints — the read side that consumes the
/// extraction pipeline output (classification + layout + field extraction)
/// and presents it as a categorized portfolio view. This is what the demo
/// surface for the board meeting renders against.
/// </summary>
public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");

        group.MapGet("/portfolio", GetPortfolio).WithName("GetPortfolio");
        group.MapGet("/documents/{assetId:guid}", GetDocumentDetail).WithName("GetDocumentDetail");
        group.MapGet("/documents/{assetId:guid}/content", GetDocumentContent).WithName("GetDocumentContent");
        group.MapGet("/insights", GetCrossDocumentInsights).WithName("GetCrossDocumentInsights");
        group.MapGet("/dashboard", GetDashboard).WithName("GetDashboard");
        group.MapGet("/review-queue", GetReviewQueue).WithName("GetReviewQueue");
        group.MapGet("/me", GetCurrentUser).WithName("GetCurrentUser");
        group.MapGet("/tenants", GetAccessibleTenants).WithName("GetAccessibleTenants");
        group.MapGet("/facilities", GetFacilities).WithName("GetFacilities");

        return routes;
    }

    private static async Task<IResult> GetDocumentContent(
        Guid assetId,
        HttpContext httpContext,
        PracticeXDbContext db,
        IDocumentStorage storage,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return Results.NotFound();

        // Slice 21 RBAC: deny content of docs outside the caller's facility
        // scope. 404 (not 403) so we don't leak the asset's existence.
        var facilityHint = await db.DocumentCandidates
            .Where(c => c.DocumentAssetId == assetId && c.TenantId == userContext.TenantId)
            .Select(c => c.FacilityHintId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!userContext.IsAuthorizedForFacility(facilityHint)) return Results.NotFound();

        string fileName = "document";
        if (asset.SourceObjectId.HasValue)
        {
            var src = await db.SourceObjects
                .Where(s => s.Id == asset.SourceObjectId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(src)) fileName = src!;
        }

        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(asset.StorageUri, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }

        // Force inline disposition. Without it, Chrome's iframe context falls
        // back to a "Open in new tab" placeholder for PDFs. Filename hint
        // ensures the browser's own download button keeps the original name.
        var safeName = fileName.Replace("\"", "").Replace("\n", "").Replace("\r", "");
        httpContext.Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
        // Allow embedding from app.practicex.ai (and pages.dev preview URLs).
        // X-Frame-Options is broader than CSP frame-ancestors but Cloudflare
        // sometimes injects DENY by default for protected resources.
        httpContext.Response.Headers["Content-Security-Policy"] =
            "frame-ancestors 'self' https://app.practicex.ai https://*.practicex-app.pages.dev";
        return Results.Stream(stream, asset.MimeType, enableRangeProcessing: true);
    }

    private static async Task<Ok<DashboardResponse>> GetDashboard(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var tenantId = userContext.TenantId;
        // Slice 21 RBAC: counts must reflect the caller's facility scope.
        var visibleCandidates = db.DocumentCandidates
            .Where(c => c.TenantId == tenantId)
            .ApplyFacilityScope(userContext);
        var visibleAssetIds = visibleCandidates.Select(c => c.DocumentAssetId);
        var visibleAssets = db.DocumentAssets
            .Where(a => a.TenantId == tenantId && visibleAssetIds.Contains(a.Id));
        var totalDocs = await visibleAssets.CountAsync(cancellationToken);
        var totalCandidates = await visibleCandidates.CountAsync(cancellationToken);
        var pendingReview = await visibleCandidates.CountAsync(
            c => c.Status == DocumentCandidateStatus.PendingReview, cancellationToken);
        var batches = await db.IngestionBatches.CountAsync(b => b.TenantId == tenantId, cancellationToken);
        var contractsTracked = await db.Contracts.CountAsync(x => x.TenantId == tenantId, cancellationToken);
        var totalSize = await visibleAssets.SumAsync(a => (long?)a.SizeBytes, cancellationToken) ?? 0L;
        var docIntelPages = await visibleAssets.SumAsync(a => (int?)a.LayoutPageCount, cancellationToken) ?? 0;

        return TypedResults.Ok(new DashboardResponse(
            TenantId: tenantId,
            Documents: totalDocs,
            Candidates: totalCandidates,
            ContractsTracked: contractsTracked,
            ReviewQueueDepth: pendingReview,
            IngestionBatches: batches,
            TotalSizeMb: Math.Round(totalSize / 1024m / 1024m, 2),
            DocIntelPagesProcessed: docIntelPages,
            EstimatedDocIntelCostUsd: Math.Round(docIntelPages * 0.001m, 4)
        ));
    }

    private static async Task<Ok<IReadOnlyList<ReviewQueueItem>>> GetReviewQueue(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var tenantId = userContext.TenantId;
        // Slice 21 RBAC: facility users only see review items for their facilities.
        var scopedCandidateIds = db.DocumentCandidates
            .Where(c => c.TenantId == tenantId && c.Status == DocumentCandidateStatus.PendingReview)
            .ApplyFacilityScope(userContext)
            .Select(c => c.Id);
        var rows = await (
            from c in db.DocumentCandidates
            join a in db.DocumentAssets on c.DocumentAssetId equals a.Id
            join s in db.SourceObjects on a.SourceObjectId equals s.Id into sj
            from s in sj.DefaultIfEmpty()
            where c.TenantId == tenantId && c.Status == DocumentCandidateStatus.PendingReview
                  && scopedCandidateIds.Contains(c.Id)
            orderby c.CreatedAt descending
            select new
            {
                c.Id,
                c.DocumentAssetId,
                c.CandidateType,
                c.Confidence,
                c.CreatedAt,
                c.OriginFilename,
                a.ExtractedSubtype,
                a.ExtractionStatus,
                a.LayoutProvider,
                AssetCreatedAt = a.CreatedAt,
                SourceName = s != null ? s.Name : null
            }
        ).Take(50).ToListAsync(cancellationToken);

        var items = rows.Select(r => new ReviewQueueItem(
            CandidateId: r.Id,
            DocumentAssetId: r.DocumentAssetId,
            FileName: r.SourceName ?? r.OriginFilename ?? "(unnamed)",
            CandidateType: r.CandidateType,
            ExtractedSubtype: r.ExtractedSubtype,
            Confidence: r.Confidence,
            UsedDocIntelligence: r.LayoutProvider != null,
            ExtractionStatus: r.ExtractionStatus,
            CreatedAt: r.CreatedAt
        )).ToList();

        return TypedResults.Ok((IReadOnlyList<ReviewQueueItem>)items);
    }

    private static async Task<Ok<CurrentUserResponse>> GetCurrentUser(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .Where(u => u.Id == userContext.UserId)
            .Select(u => new { u.Id, u.Name, u.Email, u.TenantId })
            .FirstOrDefaultAsync(cancellationToken);
        var tenant = await db.Tenants
            .Where(t => t.Id == userContext.TenantId)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync(cancellationToken);

        var name = user?.Name ?? DeriveDisplayNameFromEmail(userContext.Email) ?? "Unknown";
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(w => w.Length > 0 ? w[0] : ' '))
            .ToUpperInvariant();

        // Slice 21: surface the caller's role + accessible facility scope.
        // Frontend uses this to gate the facility selector and any
        // admin-only navigation entries.
        var role = userContext.IsSuperAdmin
            ? "super_admin"
            : userContext.IsOrgAdmin ? "org_admin" : "facility_user";
        IReadOnlyList<Guid>? facilityIds = userContext.AccessibleFacilityIds is null
            ? null
            : userContext.AccessibleFacilityIds.ToList();

        return TypedResults.Ok(new CurrentUserResponse(
            UserId: user?.Id ?? userContext.UserId,
            Name: name,
            Email: user?.Email ?? userContext.Email ?? "",
            Initials: string.IsNullOrWhiteSpace(initials) ? "??" : initials,
            TenantId: tenant?.Id ?? userContext.TenantId,
            TenantName: tenant?.Name ?? "Unknown",
            Role: role,
            IsSuperAdmin: userContext.IsSuperAdmin,
            AccessibleFacilityIds: facilityIds
        ));
    }

    private static string? DeriveDisplayNameFromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var localPart = email.Split('@', 2)[0].Trim();
        if (string.IsNullOrWhiteSpace(localPart)) return null;
        var words = localPart
            .Replace('_', ' ')
            .Replace('.', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        return string.Join(" ", words.Select(static word =>
        {
            if (word.Length == 1) return word.ToUpperInvariant();
            return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        }));
    }

    /// <summary>
    /// Slice 21 Phase 2: list of tenants the current user can switch into.
    /// Super-admin sees every tenant; everyone else sees their home tenant
    /// only. Frontend renders this as the org-switcher dropdown.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<TenantSummary>>> GetAccessibleTenants(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        IQueryable<Domain.Organization.Tenant> q = db.Tenants;
        if (!userContext.IsSuperAdmin)
        {
            q = q.Where(t => t.Id == userContext.TenantId);
        }
        var rows = await q
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummary(t.Id, t.Name, t.Status))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok((IReadOnlyList<TenantSummary>)rows);
    }

    private static async Task<Ok<IReadOnlyList<FacilitySummary>>> GetFacilities(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var counts = await db.DocumentCandidates
            .Where(c => c.TenantId == userContext.TenantId && c.FacilityHintId != null)
            .ApplyFacilityScope(userContext)
            .GroupBy(c => c.FacilityHintId!.Value)
            .Select(g => new { FacilityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FacilityId, x => x.Count, cancellationToken);

        // Slice 21 RBAC: facility users see only their own facilities in
        // the selector. Super/org admins see every facility in the tenant.
        var rows = await db.Facilities
            .Where(f => f.TenantId == userContext.TenantId)
            .Where(f => userContext.IsSuperAdmin
                     || userContext.IsOrgAdmin
                     || (userContext.AccessibleFacilityIds != null
                         && userContext.AccessibleFacilityIds.Contains(f.Id)))
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);

        var summaries = rows
            .Select(f => new FacilitySummary(
                f.Id,
                f.Code,
                f.Name,
                f.Status,
                counts.TryGetValue(f.Id, out var c) ? c : 0))
            .ToList();
        return TypedResults.Ok((IReadOnlyList<FacilitySummary>)summaries);
    }

    private static async Task<Ok<PortfolioResponse>> GetPortfolio(
        Guid? facilityId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var query = db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId)
            .Join(db.DocumentCandidates,
                a => a.Id,
                c => c.DocumentAssetId,
                (a, c) => new { Asset = a, Candidate = c });

        // Slice 21 RBAC: facility scope. Applied even if a facilityId
        // filter was passed — a facility user passing a facilityId outside
        // their scope still gets nothing.
        if (!userContext.IsSuperAdmin && !userContext.IsOrgAdmin)
        {
            var allowed = userContext.AccessibleFacilityIds;
            if (allowed is null || allowed.Count == 0)
            {
                query = query.Where(_ => false);
            }
            else
            {
                query = query.Where(x => x.Candidate.FacilityHintId.HasValue
                                      && allowed.Contains(x.Candidate.FacilityHintId.Value));
            }
        }

        if (facilityId.HasValue)
        {
            query = query.Where(x => x.Candidate.FacilityHintId == facilityId.Value);
        }

        var assets = await query.ToListAsync(cancellationToken);

        var sourceObjectIds = assets
            .Where(x => x.Asset.SourceObjectId.HasValue)
            .Select(x => x.Asset.SourceObjectId!.Value)
            .Distinct()
            .ToList();

        var sourceObjects = await db.SourceObjects
            .Where(s => sourceObjectIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var docs = assets.Select(x =>
        {
            var (expirationDate, status) = ContractStatus.Compute(x.Asset.LlmExtractedFieldsJson, today);
            var (propertyAddress, effectiveDate) = LeaseHeadline.Read(x.Asset.LlmExtractedFieldsJson);
            return new PortfolioDocument(
                DocumentAssetId: x.Asset.Id,
                DocumentCandidateId: x.Candidate.Id,
                FileName: x.Asset.SourceObjectId.HasValue && sourceObjects.TryGetValue(x.Asset.SourceObjectId.Value, out var name) ? name : "(unnamed)",
                CandidateType: x.Candidate.CandidateType,
                Family: MapToFamily(x.Candidate.CandidateType),
                ExtractedSubtype: x.Asset.ExtractedSubtype,
                Confidence: x.Candidate.Confidence,
                PageCount: x.Asset.PageCount,
                SizeBytes: x.Asset.SizeBytes,
                HasTextLayer: x.Asset.HasTextLayer,
                UsedDocIntelligence: x.Asset.LayoutProvider != null,
                LayoutPageCount: x.Asset.LayoutPageCount,
                ExtractionStatus: x.Asset.ExtractionStatus,
                ExtractionSchemaVersion: x.Asset.ExtractedSchemaVersion,
                IsTemplate: x.Asset.ExtractedIsTemplate,
                IsExecuted: x.Asset.ExtractedIsExecuted,
                ExpirationDate: expirationDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ExpirationStatus: status,
                FacilityId: x.Candidate.FacilityHintId,
                PropertyAddress: propertyAddress,
                EffectiveDate: effectiveDate,
                CreatedAt: x.Asset.CreatedAt);
        }).OrderByDescending(d => d.SizeBytes).ToList();

        // Family rollups - group by candidate type with totals.
        var families = docs
            .GroupBy(d => MapToFamily(d.CandidateType))
            .Select(g => new FamilyRollup(
                Family: g.Key,
                DocumentCount: g.Count(),
                ActiveCount: g.Count(d => d.ExpirationStatus == "active"),
                ExpiredCount: g.Count(d => d.ExpirationStatus == "expired"),
                TotalPages: g.Sum(d => d.PageCount ?? 0),
                TotalSizeMb: Math.Round(g.Sum(d => d.SizeBytes) / 1024m / 1024m, 2),
                DocIntelPagesUsed: g.Where(d => d.UsedDocIntelligence).Sum(d => d.LayoutPageCount ?? 0),
                Documents: g.Select(d => d.FileName).ToList()))
            .OrderByDescending(f => f.DocumentCount)
            .ToList();

        // Cost estimate: $0.001 per Doc Intel page (prebuilt-layout S0).
        var totalDocIntelPages = docs.Sum(d => d.LayoutPageCount ?? 0);
        var estimatedDocIntelCost = Math.Round(totalDocIntelPages * 0.001m, 4);

        return TypedResults.Ok(new PortfolioResponse(
            TenantId: userContext.TenantId,
            TotalDocuments: docs.Count,
            ActiveDocuments: docs.Count(d => d.ExpirationStatus == "active"),
            ExpiredDocuments: docs.Count(d => d.ExpirationStatus == "expired"),
            UnknownDocuments: docs.Count(d => d.ExpirationStatus == "unknown"),
            TotalPages: docs.Sum(d => d.PageCount ?? 0),
            TotalSizeMb: Math.Round(docs.Sum(d => d.SizeBytes) / 1024m / 1024m, 2),
            DocIntelPagesProcessed: totalDocIntelPages,
            EstimatedDocIntelCostUsd: estimatedDocIntelCost,
            Families: families,
            Documents: docs
        ));
    }

    private static async Task<Results<Ok<DocumentDetailResponse>, NotFound>> GetDocumentDetail(
        Guid assetId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return TypedResults.NotFound();

        var candidate = await db.DocumentCandidates
            .FirstOrDefaultAsync(c => c.DocumentAssetId == assetId && c.TenantId == userContext.TenantId, cancellationToken);

        // Slice 21 RBAC: 404 (not 403) on out-of-scope detail requests.
        if (!userContext.IsAuthorizedForFacility(candidate?.FacilityHintId)) return TypedResults.NotFound();

        string? fileName = null;
        if (asset.SourceObjectId.HasValue)
        {
            fileName = await db.SourceObjects
                .Where(s => s.Id == asset.SourceObjectId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Parse extracted_fields_json into structured response.
        ExtractedFieldsView? extractedFields = null;
        if (!string.IsNullOrEmpty(asset.ExtractedFieldsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.ExtractedFieldsJson);
                var root = doc.RootElement;
                var fields = new List<ExtractedFieldView>();
                if (root.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fieldsEl.EnumerateArray())
                    {
                        var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var confidence = f.TryGetProperty("confidence", out var c) && c.TryGetDecimal(out var cd) ? cd : 0m;
                        var sourceCitation = f.TryGetProperty("sourceCitation", out var sc) ? sc.GetString() : null;
                        var value = f.TryGetProperty("value", out var v) ? v.ToString() : null;
                        fields.Add(new ExtractedFieldView(name, value, confidence, sourceCitation));
                    }
                }
                var reasonCodes = root.TryGetProperty("reasonCodes", out var rc) && rc.ValueKind == JsonValueKind.Array
                    ? rc.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : new List<string>();
                extractedFields = new ExtractedFieldsView(fields, reasonCodes);
            }
            catch { /* leave null on parse failure */ }
        }

        // Surface LLM-extracted fields when present (Slice 13).
        // Slice 18: also surface `headline` and `field_citations` separately
        // so the UI can render the canonical-fields view first.
        ExtractedFieldsView? llmExtractedFields = null;
        Dictionary<string, object?>? headline = null;
        Dictionary<string, string>? fieldCitations = null;
        if (!string.IsNullOrEmpty(asset.LlmExtractedFieldsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.LlmExtractedFieldsJson);
                var fields = new List<ExtractedFieldView>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    // Skip the headline + field_citations from the flat field
                    // list — the UI renders them via the dedicated headline grid.
                    if (prop.Name is "headline" or "field_citations") continue;
                    var rawValue = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.ToString();
                    fields.Add(new ExtractedFieldView(prop.Name, rawValue, 0.95m, null));
                }
                llmExtractedFields = new ExtractedFieldsView(fields, ["llm_extracted"]);

                if (doc.RootElement.TryGetProperty("headline", out var hl) &&
                    hl.ValueKind == JsonValueKind.Object)
                {
                    headline = new Dictionary<string, object?>();
                    foreach (var prop in hl.EnumerateObject())
                    {
                        headline[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Null => null,
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.TryGetInt64(out var i) ? (object)i : prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => prop.Value.ToString()
                        };
                    }
                }
                if (doc.RootElement.TryGetProperty("field_citations", out var fc) &&
                    fc.ValueKind == JsonValueKind.Object)
                {
                    fieldCitations = new Dictionary<string, string>();
                    foreach (var prop in fc.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            fieldCitations[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }
                }
            }
            catch { /* leave null */ }
        }

        // Pull a text snippet for the demo's "what we read" panel. Prefer
        // Doc Intel's layout fullText when it ran (best fidelity); otherwise
        // fall back to the locally-extracted text saved during ingestion
        // (PdfPig for digital PDFs, OpenXml for DOCX).
        string? layoutSnippet = null;
        if (!string.IsNullOrEmpty(asset.LayoutJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.LayoutJson);
                if (doc.RootElement.TryGetProperty("fullText", out var ft))
                {
                    var text = ft.GetString() ?? "";
                    layoutSnippet = text.Length > 1500 ? text[..1500] + "..." : text;
                }
            }
            catch { /* ignore */ }
        }
        if (string.IsNullOrEmpty(layoutSnippet) && !string.IsNullOrEmpty(asset.ExtractedFullText))
        {
            var text = asset.ExtractedFullText;
            layoutSnippet = text.Length > 1500 ? text[..1500] + "..." : text;
        }

        return TypedResults.Ok(new DocumentDetailResponse(
            DocumentAssetId: asset.Id,
            FileName: fileName ?? "(unnamed)",
            CandidateType: candidate?.CandidateType,
            Confidence: candidate?.Confidence,
            ExtractedSubtype: asset.ExtractedSubtype,
            ExtractedSchemaVersion: asset.ExtractedSchemaVersion,
            ExtractorName: asset.ExtractorName,
            ExtractionStatus: asset.ExtractionStatus,
            IsTemplate: asset.ExtractedIsTemplate,
            IsExecuted: asset.ExtractedIsExecuted,
            PageCount: asset.PageCount,
            HasTextLayer: asset.HasTextLayer,
            LayoutProvider: asset.LayoutProvider,
            LayoutModel: asset.LayoutModel,
            LayoutPageCount: asset.LayoutPageCount,
            LayoutSnippet: layoutSnippet,
            ExtractedFields: extractedFields,
            LlmExtractedFields: llmExtractedFields,
            LlmModel: asset.LlmExtractorModel,
            LlmExtractedAt: asset.LlmExtractedAt,
            Headline: headline,
            FieldCitations: fieldCitations,
            NarrativeBriefMd: asset.LlmNarrativeMd,
            NarrativeModel: asset.LlmNarrativeModel,
            NarrativeExtractedAt: asset.LlmNarrativeExtractedAt,
            CreatedAt: asset.CreatedAt
        ));
    }

    private static async Task<Ok<CrossDocumentInsights>> GetCrossDocumentInsights(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        // Pull every asset for this tenant; prefer LLM-extracted JSON when
        // present (clean entity names, structured lists), fall back to the
        // regex output for docs that haven't been LLM-refined yet.
        // Slice 21 RBAC: cross-doc insights must respect facility scope —
        // a Parag-scoped user must not see Synexar landlords/tenants/etc
        // mingled into their insights view.
        var visibleAssetIds = db.DocumentCandidates
            .Where(c => c.TenantId == userContext.TenantId)
            .ApplyFacilityScope(userContext)
            .Select(c => c.DocumentAssetId);
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId &&
                        (a.LlmExtractedFieldsJson != null || a.ExtractedFieldsJson != null) &&
                        visibleAssetIds.Contains(a.Id))
            .ToListAsync(cancellationToken);

        var addressByDoc = new Dictionary<string, string>();
        // (normalized "address|suite") -> max sqft seen. Amendments often
        // expand a suite (e.g. 5,000 -> 8,000 sqft); summing every amendment's
        // value triple-counts the same physical space. Keep the largest.
        var sqftBySuite = new Dictionary<string, decimal>();
        var amendmentChains = new Dictionary<string, List<string>>();
        var entities = new EntityRegistry();

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        foreach (var asset in assets)
        {
            var docName = (asset.SourceObjectId.HasValue && sourceNames.TryGetValue(asset.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";

            // LLM JSON has the cleanest entity strings; use that when available.
            if (!string.IsNullOrEmpty(asset.LlmExtractedFieldsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(asset.LlmExtractedFieldsJson);
                    var root = doc.RootElement;
                    AbsorbLlmInsights(root, docName, entities, sqftBySuite, addressByDoc, amendmentChains);
                    continue;  // LLM data wins, skip regex for this doc
                }
                catch { /* fall through to regex */ }
            }

            if (string.IsNullOrEmpty(asset.ExtractedFieldsJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(asset.ExtractedFieldsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("fields", out var fields)) continue;
                AbsorbRegexInsights(fields, docName, entities, sqftBySuite, addressByDoc, amendmentChains);
            }
            catch { /* skip malformed */ }
        }

        var totalSqft = sqftBySuite.Values.Sum();
        var landlordList = entities.Landlords();
        var tenantList = entities.Tenants();
        // Counterparties already shown as landlord or tenant are noise.
        var counterpartyList = entities.Counterparties(excludeKeys: entities.PrincipalKeys());

        return TypedResults.Ok(new CrossDocumentInsights(
            TotalRentableSqft: totalSqft > 0 ? totalSqft : null,
            UniqueLandlords: landlordList,
            UniqueTenants: tenantList,
            UniqueCounterparties: counterpartyList,
            AmendmentChains: amendmentChains
                .Select(kvp => new AmendmentChain(kvp.Key, kvp.Value))
                .OrderByDescending(c => c.Amendments.Count)
                .ToList(),
            DocumentAddresses: addressByDoc
        ));
    }

    private static void AbsorbLlmInsights(
        JsonElement root,
        string docName,
        EntityRegistry entities,
        Dictionary<string, decimal> sqftBySuite,
        Dictionary<string, string> addressByDoc,
        Dictionary<string, List<string>> amendmentChains)
    {
        // Lease family: top-level landlord / tenant / premises[].
        if (root.TryGetProperty("landlord", out var landlord) && landlord.ValueKind == JsonValueKind.String)
        {
            entities.AddLandlord(landlord.GetString());
        }
        if (root.TryGetProperty("tenant", out var tenant) && tenant.ValueKind == JsonValueKind.String)
        {
            entities.AddTenant(tenant.GetString());
        }
        if (root.TryGetProperty("premises", out var premises) && premises.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in premises.EnumerateArray())
            {
                var sqft = p.TryGetProperty("rentable_square_feet", out var sqEl) &&
                           sqEl.ValueKind == JsonValueKind.Number &&
                           sqEl.TryGetDecimal(out var sq) ? sq : 0m;
                var street = p.TryGetProperty("street_address", out var stEl) &&
                             stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                var suite = p.TryGetProperty("suite", out var suEl) &&
                            suEl.ValueKind == JsonValueKind.String ? suEl.GetString() : null;
                RecordSuiteSqft(sqftBySuite, street, suite, docName, sqft);
                if (!string.IsNullOrWhiteSpace(street)) addressByDoc[docName] = street!;
            }
        }

        // Lease amendment: parent_agreement_date references the original lease.
        if (root.TryGetProperty("parent_agreement_date", out var parent) &&
            parent.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(parent.GetString()))
        {
            var key = $"Lease Agreement dated {parent.GetString()}";
            if (!amendmentChains.TryGetValue(key, out var chain))
            {
                chain = new List<string>();
                amendmentChains[key] = chain;
            }
            if (!chain.Contains(docName)) chain.Add(docName);
        }

        // NDA / employment / call coverage: parties[] (objects with "name").
        if (root.TryGetProperty("parties", out var parties) && parties.ValueKind == JsonValueKind.Array)
        {
            foreach (var party in parties.EnumerateArray())
            {
                if (party.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                {
                    entities.AddCounterparty(nm.GetString());
                }
            }
        }
    }

    private static void AbsorbRegexInsights(
        JsonElement fields,
        string docName,
        EntityRegistry entities,
        Dictionary<string, decimal> sqftBySuite,
        Dictionary<string, string> addressByDoc,
        Dictionary<string, List<string>> amendmentChains)
    {
        foreach (var f in fields.EnumerateArray())
        {
            var name = f.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
            if (!f.TryGetProperty("value", out var v) || v.ValueKind == JsonValueKind.Null) continue;

            if (name == "premises" && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in v.EnumerateArray())
                {
                    var sqft = p.TryGetProperty("RentableSquareFeet", out var s) && s.TryGetDecimal(out var sd) ? sd : 0m;
                    var street = p.TryGetProperty("StreetAddress", out var st) ? st.GetString() : null;
                    var suite = p.TryGetProperty("Suite", out var su) ? su.GetString() : null;
                    RecordSuiteSqft(sqftBySuite, street, suite, docName, sqft);
                    if (!string.IsNullOrWhiteSpace(street)) addressByDoc[docName] = street!;
                }
            }
            else if (name == "landlord" && v.ValueKind == JsonValueKind.String)
            {
                entities.AddLandlord(v.GetString());
            }
            else if (name == "tenant" && v.ValueKind == JsonValueKind.String)
            {
                entities.AddTenant(v.GetString());
            }
            else if (name == "amends" && v.ValueKind == JsonValueKind.Object)
            {
                var parentTitle = v.TryGetProperty("ParentDocumentTitle", out var pt) ? pt.GetString() : null;
                if (!string.IsNullOrWhiteSpace(parentTitle))
                {
                    if (!amendmentChains.TryGetValue(parentTitle!, out var chain))
                    {
                        chain = new List<string>();
                        amendmentChains[parentTitle!] = chain;
                    }
                    chain.Add(docName);
                }
            }
            else if (name == "parties" && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var party in v.EnumerateArray())
                {
                    var partyName = party.TryGetProperty("Name", out var pn) ? pn.GetString() : null;
                    entities.AddCounterparty(partyName);
                }
            }
        }
    }

    private static void RecordSuiteSqft(
        Dictionary<string, decimal> sqftBySuite,
        string? street,
        string? suite,
        string docName,
        decimal sqft)
    {
        if (sqft <= 0m) return;
        // Build a stable key per physical space. Fall back to docName when we
        // have no address (a worst-case the LLM didn't surface), so at least
        // we don't merge two addressless leases into one bucket.
        var addr = (street ?? "").Trim().ToLowerInvariant();
        var ste = (suite ?? "").Trim().ToLowerInvariant();
        var key = !string.IsNullOrEmpty(addr)
            ? $"{addr}|{ste}"
            : $"_doc:{docName}|{ste}";
        // Take the max — amendments expand suites, they don't add new ones.
        if (!sqftBySuite.TryGetValue(key, out var prior) || sqft > prior)
        {
            sqftBySuite[key] = sqft;
        }
    }

    /// <summary>
    /// Collects entity strings (landlord / tenant / counterparty) and collapses
    /// case + punctuation + entity-suffix variants under a single canonical
    /// display name. "Eagle Physicians, P.A." / "EAGLE PHYSICIANS AND
    /// ASSOCIATES, PA" / "Eagle Physicians and Associates, P.A." normalize to
    /// the same key; we keep the longest variant as the display label.
    /// </summary>
    private sealed class EntityRegistry
    {
        private readonly Dictionary<string, string> _landlords = new();
        private readonly Dictionary<string, string> _tenants = new();
        private readonly Dictionary<string, string> _counterparties = new();

        public void AddLandlord(string? raw) => Add(_landlords, raw);
        public void AddTenant(string? raw) => Add(_tenants, raw);
        public void AddCounterparty(string? raw) => Add(_counterparties, raw);

        public IReadOnlyList<string> Landlords() =>
            _landlords.Values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        public IReadOnlyList<string> Tenants() =>
            _tenants.Values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        public IReadOnlyList<string> Counterparties(IReadOnlySet<string> excludeKeys) =>
            _counterparties
                .Where(kvp => !MatchesAnyPrincipal(kvp.Key, excludeKeys))
                .Select(kvp => kvp.Value)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public IReadOnlySet<string> PrincipalKeys()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in _landlords.Keys) set.Add(k);
            foreach (var k in _tenants.Keys) set.Add(k);
            return set;
        }

        private static bool MatchesAnyPrincipal(string counterpartyKey, IReadOnlySet<string> principals)
        {
            foreach (var p in principals)
            {
                if (p == counterpartyKey) return true;
                if (IsTokenPrefix(p, counterpartyKey)) return true;
                if (IsTokenPrefix(counterpartyKey, p)) return true;
            }
            return false;
        }

        private static void Add(Dictionary<string, string> bucket, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var display = raw.Trim();
            var key = Normalize(display);
            if (string.IsNullOrEmpty(key)) return;

            // Prefix-merge: "eagle physicians" and "eagle physicians associates"
            // refer to the same entity; collapse onto the longer key.
            string? mergedFromExisting = null;
            foreach (var existingKey in bucket.Keys.ToList())
            {
                if (existingKey == key) break;
                if (IsTokenPrefix(existingKey, key))
                {
                    // New key extends existing — promote new key, drop short one.
                    mergedFromExisting = existingKey;
                    break;
                }
                if (IsTokenPrefix(key, existingKey))
                {
                    // Existing key already covers new key — fold into it.
                    var existingDisplay = bucket[existingKey];
                    if (display.Length > existingDisplay.Length)
                    {
                        bucket[existingKey] = display;
                    }
                    return;
                }
            }
            if (mergedFromExisting is not null)
            {
                var displaced = bucket[mergedFromExisting];
                bucket.Remove(mergedFromExisting);
                if (displaced.Length > display.Length) display = displaced;
            }

            if (!bucket.TryGetValue(key, out var existing) || display.Length > existing.Length)
            {
                bucket[key] = display;
            }
        }

        private static bool IsTokenPrefix(string shorter, string longer)
        {
            if (shorter.Length >= longer.Length) return false;
            return longer.StartsWith(shorter + " ", StringComparison.Ordinal);
        }

        private static readonly string[] EntitySuffixes =
        {
            "p a", "pa", "pllc", "llc", "lp", "llp", "inc", "incorporated",
            "corp", "corporation", "co", "ltd", "limited", "the"
        };

        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            // Lowercase, strip punctuation, collapse whitespace.
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '&' || ch == '/' || ch == '.' || ch == ',')
                    sb.Append(' ');
            }
            var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            // Tokenize, drop entity suffixes and the conjunction "and".
            var tokens = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t != "and")
                .ToList();
            // Remove trailing entity suffixes (one or two tokens).
            while (tokens.Count > 0 && EntitySuffixes.Contains(tokens[^1]))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            // Catch "p a" as a two-token suffix.
            if (tokens.Count >= 2 && tokens[^2] == "p" && tokens[^1] == "a")
            {
                tokens.RemoveAt(tokens.Count - 1);
                tokens.RemoveAt(tokens.Count - 1);
            }
            return string.Join(' ', tokens);
        }
    }

    private static string MapToFamily(string candidateType) => candidateType switch
    {
        DocumentCandidateTypes.Lease or
        DocumentCandidateTypes.LeaseAmendment or
        DocumentCandidateTypes.LeaseLoi => "lease",

        DocumentCandidateTypes.EmployeeAgreement or
        DocumentCandidateTypes.Amendment => "employment_governance",

        DocumentCandidateTypes.Nda => "nda",

        DocumentCandidateTypes.Bylaws => "governance",

        DocumentCandidateTypes.CallCoverageAgreement => "scheduling",

        DocumentCandidateTypes.ServiceAgreement or
        DocumentCandidateTypes.VendorContract => "vendor_services",

        DocumentCandidateTypes.PayerContract => "payer",
        DocumentCandidateTypes.ProcessorAgreement => "compliance",
        DocumentCandidateTypes.FeeSchedule => "fee_schedule",
        DocumentCandidateTypes.OperationalData => "operational_data",

        // Synexar / early-stage company taxonomy.
        DocumentCandidateTypes.BoardResolution or
        DocumentCandidateTypes.FoundersMeeting => "corp_governance",
        DocumentCandidateTypes.EquityGrant or
        DocumentCandidateTypes.TermSheet => "equity",
        DocumentCandidateTypes.IpAssignment => "ip",
        DocumentCandidateTypes.CorpFormation => "corp_formation",
        DocumentCandidateTypes.RegulatoryFiling => "regulatory",
        DocumentCandidateTypes.PrivacyPolicy or
        DocumentCandidateTypes.TermsOfService => "policy",

        _ => "unclassified"
    };
}

// ----------------------------------------------------------------------------
// Response DTOs
// ----------------------------------------------------------------------------

public sealed record PortfolioResponse(
    Guid TenantId,
    int TotalDocuments,
    int ActiveDocuments,
    int ExpiredDocuments,
    int UnknownDocuments,
    int TotalPages,
    decimal TotalSizeMb,
    int DocIntelPagesProcessed,
    decimal EstimatedDocIntelCostUsd,
    IReadOnlyList<FamilyRollup> Families,
    IReadOnlyList<PortfolioDocument> Documents);

public sealed record FamilyRollup(
    string Family,
    int DocumentCount,
    int ActiveCount,
    int ExpiredCount,
    int TotalPages,
    decimal TotalSizeMb,
    int DocIntelPagesUsed,
    IReadOnlyList<string> Documents);

public sealed record PortfolioDocument(
    Guid DocumentAssetId,
    Guid DocumentCandidateId,
    string FileName,
    string CandidateType,
    string Family,
    string? ExtractedSubtype,
    decimal Confidence,
    int? PageCount,
    long SizeBytes,
    bool? HasTextLayer,
    bool UsedDocIntelligence,
    int? LayoutPageCount,
    string? ExtractionStatus,
    string? ExtractionSchemaVersion,
    bool? IsTemplate,
    bool? IsExecuted,
    string? ExpirationDate,        // yyyy-MM-dd or null when no canonical date data
    string ExpirationStatus,       // "active" | "expired" | "unknown"
    Guid? FacilityId,
    string? PropertyAddress,       // headline.premises_address — used to group leases by building
    string? EffectiveDate,         // yyyy-MM-dd — for sorting versions of the same property
    DateTimeOffset CreatedAt);

public sealed record DocumentDetailResponse(
    Guid DocumentAssetId,
    string FileName,
    string? CandidateType,
    decimal? Confidence,
    string? ExtractedSubtype,
    string? ExtractedSchemaVersion,
    string? ExtractorName,
    string? ExtractionStatus,
    bool? IsTemplate,
    bool? IsExecuted,
    int? PageCount,
    bool? HasTextLayer,
    string? LayoutProvider,
    string? LayoutModel,
    int? LayoutPageCount,
    string? LayoutSnippet,
    ExtractedFieldsView? ExtractedFields,
    ExtractedFieldsView? LlmExtractedFields,
    string? LlmModel,
    DateTimeOffset? LlmExtractedAt,
    IReadOnlyDictionary<string, object?>? Headline,
    IReadOnlyDictionary<string, string>? FieldCitations,
    string? NarrativeBriefMd,
    string? NarrativeModel,
    DateTimeOffset? NarrativeExtractedAt,
    DateTimeOffset CreatedAt);

public sealed record ExtractedFieldsView(
    IReadOnlyList<ExtractedFieldView> Fields,
    IReadOnlyList<string> ReasonCodes);

public sealed record ExtractedFieldView(
    string Name,
    string? Value,
    decimal Confidence,
    string? SourceCitation);

public sealed record CrossDocumentInsights(
    decimal? TotalRentableSqft,
    IReadOnlyList<string> UniqueLandlords,
    IReadOnlyList<string> UniqueTenants,
    IReadOnlyList<string> UniqueCounterparties,
    IReadOnlyList<AmendmentChain> AmendmentChains,
    IReadOnlyDictionary<string, string> DocumentAddresses);

public sealed record AmendmentChain(
    string ParentDocumentTitle,
    IReadOnlyList<string> Amendments);

public sealed record DashboardResponse(
    Guid TenantId,
    int Documents,
    int Candidates,
    int ContractsTracked,
    int ReviewQueueDepth,
    int IngestionBatches,
    decimal TotalSizeMb,
    int DocIntelPagesProcessed,
    decimal EstimatedDocIntelCostUsd);

public sealed record ReviewQueueItem(
    Guid CandidateId,
    Guid DocumentAssetId,
    string FileName,
    string CandidateType,
    string? ExtractedSubtype,
    decimal Confidence,
    bool UsedDocIntelligence,
    string? ExtractionStatus,
    DateTimeOffset CreatedAt);

public sealed record CurrentUserResponse(
    Guid UserId,
    string Name,
    string Email,
    string Initials,
    Guid TenantId,
    string TenantName,
    string Role,                                       // "super_admin" | "org_admin" | "facility_user"
    bool IsSuperAdmin,
    IReadOnlyList<Guid>? AccessibleFacilityIds);       // null = unrestricted in tenant

public sealed record FacilitySummary(
    Guid Id,
    string Code,
    string Name,
    string Status,
    int DocumentCount);

public sealed record TenantSummary(
    Guid Id,
    string Name,
    string Status);

/// <summary>
/// Reads the canonical-headline JSON written by Stage-2 LLM extraction and
/// computes whether a document is currently active, expired, or unknown.
/// "Active" means we have a derived expiration date in the future; "expired"
/// means it's in the past; "unknown" means we have no headline date data.
/// Lease/employment/scheduling use expiration_date or commencement+term;
/// NDAs use effective_date + discussion_term_months.
/// </summary>
internal static class ContractStatus
{
    public static (DateOnly? Expiration, string Status) Compute(string? llmExtractedFieldsJson, DateOnly today)
    {
        if (string.IsNullOrEmpty(llmExtractedFieldsJson)) return (null, "unknown");

        JsonElement headline;
        try
        {
            using var doc = JsonDocument.Parse(llmExtractedFieldsJson);
            if (!doc.RootElement.TryGetProperty("headline", out var hl) ||
                hl.ValueKind != JsonValueKind.Object)
                return (null, "unknown");
            headline = hl.Clone();
        }
        catch { return (null, "unknown"); }

        // Direct expiration_date wins (lease/employment).
        var expiration = ReadDate(headline, "expiration_date");
        if (expiration is null)
        {
            // Derive from commencement_date + initial_term_months / term_months.
            var start = ReadDate(headline, "commencement_date") ?? ReadDate(headline, "effective_date");
            var months = ReadInt(headline, "initial_term_months") ?? ReadInt(headline, "term_months");
            // For NDAs, use discussion_term_months as the active-period proxy.
            if (months is null) months = ReadInt(headline, "discussion_term_months");
            if (start is { } s && months is { } m && m > 0)
            {
                expiration = s.AddMonths(m);
            }
        }

        if (expiration is null) return (null, "unknown");
        return (expiration, expiration.Value > today ? "active" : "expired");
    }

    private static DateOnly? ReadDate(JsonElement headline, string key)
    {
        if (!headline.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
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
}

/// <summary>
/// Pulls the canonical property address (from the headline block) and the
/// document's effective date from the LLM-extracted JSON. Both are used by
/// the frontend to collapse multiple lease versions onto a single property.
/// </summary>
internal static class LeaseHeadline
{
    public static (string? PropertyAddress, string? EffectiveDate) Read(string? llmExtractedFieldsJson)
    {
        if (string.IsNullOrEmpty(llmExtractedFieldsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(llmExtractedFieldsJson);
            var root = doc.RootElement;
            string? address = null;
            if (root.TryGetProperty("headline", out var hl) && hl.ValueKind == JsonValueKind.Object
                && hl.TryGetProperty("premises_address", out var addr) && addr.ValueKind == JsonValueKind.String)
            {
                address = addr.GetString();
            }
            string? effective = null;
            if (root.TryGetProperty("effective_date", out var ed) && ed.ValueKind == JsonValueKind.String)
            {
                effective = ed.GetString();
            }
            return (NullIfBlank(address), NullIfBlank(effective));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
