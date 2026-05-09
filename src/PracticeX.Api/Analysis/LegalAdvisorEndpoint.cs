using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Discovery.Llm;
using PracticeX.Domain.Audit;
using PracticeX.Domain.Documents;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Api.Analysis;

/// <summary>
/// Slice 20 — Legal Advisor Agent (premium "Counsel's Memo" surface).
///
/// Stage A (per-document memo). The model adopts a General-Counsel posture
/// (adversarial, risk-first, redline-ready) and produces a sectioned
/// markdown memo: posture snapshot + risk score, issue register, family
/// overlay findings, proposed redlines, material disclosures, counterparty
/// posture, action items, plain-English summary. Saved to
/// <c>document_assets.legal_memo_md</c>.
///
/// Stage B (per-document JSON). A precise extractor reads the markdown
/// memo as ground truth and emits the structured legal_memo_v1 JSON
/// (issues array, redlines, disclosures, action items, risk score).
/// Saved to <c>document_assets.legal_memo_json</c>.
///
/// Stage C (counsel brief). Per-tenant cross-document synthesis — distinct
/// from PortfolioBrief which is the partner-friendly executive view. The
/// counsel brief is read by counsel/audit/M&A diligence. Saved to
/// <c>doc.counsel_briefs</c>.
///
/// Every response surface attaches the application-level disclaimer banner
/// upstream — the model is instructed not to duplicate it in body content.
/// </summary>
public static class LegalAdvisorEndpoint
{
    public static IEndpointRouteBuilder MapLegalAdvisorEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/legal-advisor").WithTags("LegalAdvisor");
        group.MapPost("/memos/{assetId:guid}", GenerateMemo).WithName("GenerateLegalMemo");
        group.MapGet("/memos/{assetId:guid}", GetMemo).WithName("GetLegalMemo");
        group.MapPost("/memos-batch", BatchGenerateMemos).WithName("BatchGenerateLegalMemos");
        group.MapGet("/portfolio", GetMemoPortfolio).WithName("GetLegalMemoPortfolio");
        group.MapPost("/counsel-brief", GenerateCounselBrief).WithName("GenerateCounselBrief");
        group.MapGet("/counsel-brief", GetCounselBrief).WithName("GetCounselBrief");
        return routes;
    }

    // 80K input chars matches the existing two-stage pipeline (Sonnet 4.6
    // 200K context, leaving room for the brief, headline, overlay, and
    // memo response). 8192-token output covers a 2,000-2,800 word memo.
    private const int MaxInputChars = 80_000;
    private const int StageAMaxTokens = 8192;
    private const int StageBMaxTokens = 6144;
    private const int CounselBriefMaxTokens = 8192;
    private const double StageATemperature = 0.2;
    private const double StageBTemperature = 0.1;
    private const double CounselBriefTemperature = 0.3;

    private const string SystemPromptStageA = """
        You are the General Counsel of the organization that owns this
        contract. Your audience is your CEO and your board. Your posture is
        adversarial, risk-first, transactionally seasoned. Follow the
        8-section structural template in the user message exactly. Output
        markdown; no JSON, no code fences. Stay grounded in the source
        document; use sanctioned hedges instead of guessing. Do NOT include
        legal-disclaimer paragraphs in the body — the application surface
        attaches one.
        """;

    private const string SystemPromptStageB = """
        You are a precise JSON extractor. Read the Counsel's Memo in the
        user message and emit a single JSON object that matches the schema.
        The memo is ground truth — do not infer beyond it. No prose, no
        markdown, no code fences — JSON only.
        """;

    private const string SystemPromptCounselBrief = """
        You are the General Counsel synthesizing a corpus of per-document
        Counsel's Memos into a single board-grade Counsel's Brief. Posture
        is risk-first, cross-document. Follow the 9-section template
        exactly. Output markdown; no JSON, no code fences. Cite source
        documents in parentheses where claims need backup. Do NOT include
        legal-disclaimer paragraphs in the body.
        """;

    // ------------------------------------------------------------------
    // Per-document memo
    // ------------------------------------------------------------------

    private static async Task<Results<Ok<LegalMemoResult>, NotFound, BadRequest<ProblemSummary>>> GenerateMemo(
        Guid assetId,
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<LegalAdvisorMarker> logger,
        CancellationToken cancellationToken)
    {
        if (!llm.IsConfigured)
        {
            return TypedResults.BadRequest(new ProblemSummary("llm_not_configured",
                "LLM provider isn't configured. Set OpenRouter:Enabled=true and OpenRouter:ApiKey via user-secrets."));
        }

        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return TypedResults.NotFound();

        var candidate = await db.DocumentCandidates
            .FirstOrDefaultAsync(c => c.DocumentAssetId == assetId && c.TenantId == userContext.TenantId, cancellationToken);
        var candidateType = candidate?.CandidateType ?? "unknown";

        var docText = ResolveDocumentText(asset);
        if (string.IsNullOrWhiteSpace(docText))
        {
            return TypedResults.BadRequest(new ProblemSummary("no_text",
                "No extracted text available for this document. Re-ingest first."));
        }

        var fileName = await ResolveFileName(db, asset, cancellationToken);

        try
        {
            var (tIn, tOut) = await RunMemoTwoStageAsync(asset, candidateType, fileName, docText, llm, cancellationToken);

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "legal_advisor.memo.generate",
                ResourceType = "document_asset",
                ResourceId = asset.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    candidateType,
                    family = PromptLoader.ResolveFamily(candidateType),
                    memoModel = asset.LegalMemoModel,
                    riskScore = asset.LegalMemoRiskScore,
                    tokensIn = tIn,
                    tokensOut = tOut,
                    inputChars = docText.Length
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new LegalMemoResult(
                Status: asset.LegalMemoStatus ?? "completed",
                Model: asset.LegalMemoModel ?? "",
                RiskScore: asset.LegalMemoRiskScore,
                TokensIn: tIn,
                TokensOut: tOut,
                LatencyMs: asset.LegalMemoLatencyMs ?? 0,
                MemoMd: asset.LegalMemoMd ?? "",
                MemoJson: asset.LegalMemoJson ?? "{}"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Counsel's memo generation failed for asset {AssetId}", assetId);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemSummary("memo_failed", ex.Message));
        }
    }

    private static async Task<Results<Ok<LegalMemoResult>, NotFound>> GetMemo(
        Guid assetId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return TypedResults.NotFound();
        if (string.IsNullOrEmpty(asset.LegalMemoMd)) return TypedResults.NotFound();

        return TypedResults.Ok(new LegalMemoResult(
            Status: asset.LegalMemoStatus ?? "completed",
            Model: asset.LegalMemoModel ?? "",
            RiskScore: asset.LegalMemoRiskScore,
            TokensIn: asset.LegalMemoTokensIn ?? 0,
            TokensOut: asset.LegalMemoTokensOut ?? 0,
            LatencyMs: asset.LegalMemoLatencyMs ?? 0,
            MemoMd: asset.LegalMemoMd,
            MemoJson: asset.LegalMemoJson ?? "{}"));
    }

    private static async Task<Ok<BatchExtractionResult>> BatchGenerateMemos(
        bool? force,
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<LegalAdvisorMarker> logger,
        CancellationToken cancellationToken)
    {
        if (!llm.IsConfigured)
        {
            return TypedResults.Ok(new BatchExtractionResult(
                Total: 0, Refined: 0, Skipped: 0, Failed: 0, TotalTokensIn: 0, TotalTokensOut: 0,
                LatencyMs: 0, Notes: "LLM not configured."));
        }

        // We require a Stage-1 brief to be present (the memo reads it as
        // grounding context) — the alternative is doing the brief first,
        // which we already do via /api/analysis/llm-extract-batch.
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId &&
                        a.LlmNarrativeMd != null)
            .ToListAsync(cancellationToken);

        var sourceNameById = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int generated = 0, skipped = 0, failed = 0;
        int totalIn = 0, totalOut = 0;
        var doForce = force == true;

        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!doForce && !string.IsNullOrEmpty(asset.LegalMemoMd))
            {
                skipped++;
                continue;
            }

            var candidate = await db.DocumentCandidates
                .FirstOrDefaultAsync(c => c.DocumentAssetId == asset.Id && c.TenantId == userContext.TenantId, cancellationToken);
            var candidateType = candidate?.CandidateType ?? "unknown";

            var docText = ResolveDocumentText(asset);
            if (string.IsNullOrWhiteSpace(docText))
            {
                skipped++;
                continue;
            }

            var fileName = (asset.SourceObjectId.HasValue
                && sourceNameById.TryGetValue(asset.SourceObjectId.Value, out var fn))
                ? fn : "(unnamed)";

            try
            {
                var (tIn, tOut) = await RunMemoTwoStageAsync(asset, candidateType, fileName, docText, llm, cancellationToken);
                totalIn += tIn;
                totalOut += tOut;
                generated++;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Counsel's memo failed for asset {AssetId}", asset.Id);
                failed++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        sw.Stop();

        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = userContext.TenantId,
            ActorType = "user",
            ActorId = userContext.UserId,
            EventType = "legal_advisor.memo.batch",
            ResourceType = "tenant",
            ResourceId = userContext.TenantId,
            MetadataJson = JsonSerializer.Serialize(new
            {
                total = assets.Count,
                generated,
                skipped,
                failed,
                totalTokensIn = totalIn,
                totalTokensOut = totalOut,
                latencyMs = sw.ElapsedMilliseconds,
                force = doForce
            }),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new BatchExtractionResult(
            Total: assets.Count,
            Refined: generated,
            Skipped: skipped,
            Failed: failed,
            TotalTokensIn: totalIn,
            TotalTokensOut: totalOut,
            LatencyMs: sw.ElapsedMilliseconds,
            Notes: null));
    }

    // ------------------------------------------------------------------
    // Portfolio (per-doc summary list, sorted by risk)
    // ------------------------------------------------------------------

    private static async Task<Ok<LegalAdvisorPortfolioResponse>> GetMemoPortfolio(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId)
            .ToListAsync(cancellationToken);

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var candidatesByAsset = await db.DocumentCandidates
            .Where(c => c.TenantId == userContext.TenantId)
            .ToDictionaryAsync(c => c.DocumentAssetId, c => c.CandidateType, cancellationToken);

        var rows = assets.Select(a =>
        {
            var fileName = (a.SourceObjectId.HasValue && sourceNames.TryGetValue(a.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";
            var candidateType = candidatesByAsset.GetValueOrDefault(a.Id, "unknown");
            var (rating, headline, topIssue) = ParseMemoSummary(a.LegalMemoJson);
            return new LegalAdvisorPortfolioRow(
                DocumentAssetId: a.Id,
                FileName: fileName,
                CandidateType: candidateType,
                Family: PromptLoader.ResolveFamily(candidateType).ToLowerInvariant(),
                IsExecuted: a.ExtractedIsExecuted,
                MemoStatus: a.LegalMemoStatus,
                RiskScore: a.LegalMemoRiskScore,
                RiskRating: rating,
                Headline: headline,
                TopIssueTitle: topIssue,
                MemoModel: a.LegalMemoModel,
                MemoExtractedAt: a.LegalMemoExtractedAt
            );
        })
        .OrderByDescending(r => r.RiskScore ?? -1m)
        .ThenBy(r => r.FileName)
        .ToList();

        var withMemo = rows.Where(r => r.RiskScore.HasValue).ToList();
        var counts = new LegalAdvisorPortfolioCounts(
            Total: rows.Count,
            WithMemo: withMemo.Count,
            Severe: withMemo.Count(r => r.RiskScore >= 81m),
            High: withMemo.Count(r => r.RiskScore >= 61m && r.RiskScore < 81m),
            Elevated: withMemo.Count(r => r.RiskScore >= 41m && r.RiskScore < 61m),
            Modest: withMemo.Count(r => r.RiskScore >= 21m && r.RiskScore < 41m),
            Low: withMemo.Count(r => r.RiskScore < 21m));

        return TypedResults.Ok(new LegalAdvisorPortfolioResponse(
            Counts: counts,
            Rows: rows,
            Disclaimer: Disclaimer));
    }

    // ------------------------------------------------------------------
    // Cross-document Counsel's Brief
    // ------------------------------------------------------------------

    private static async Task<Results<Ok<CounselBriefResponse>, BadRequest<ProblemSummary>>> GenerateCounselBrief(
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<LegalAdvisorMarker> logger,
        CancellationToken cancellationToken)
    {
        if (!llm.IsConfigured)
        {
            return TypedResults.BadRequest(new ProblemSummary("llm_not_configured",
                "LLM provider isn't configured."));
        }

        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId &&
                        a.LegalMemoJson != null)
            .ToListAsync(cancellationToken);

        if (assets.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemSummary("no_memos",
                "No per-document Counsel's Memos available yet. Run /api/legal-advisor/memos-batch first."));
        }

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var candidatesByAsset = await db.DocumentCandidates
            .Where(c => c.TenantId == userContext.TenantId)
            .ToDictionaryAsync(c => c.DocumentAssetId, c => c.CandidateType, cancellationToken);

        var memos = assets.Select(a =>
        {
            var fileName = (a.SourceObjectId.HasValue && sourceNames.TryGetValue(a.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";
            var candidateType = candidatesByAsset.GetValueOrDefault(a.Id, "unknown");
            JsonElement? memo = null;
            try
            {
                using var doc = JsonDocument.Parse(a.LegalMemoJson!);
                memo = doc.RootElement.Clone();
            }
            catch { /* skip bad json */ }

            return new
            {
                file_name = fileName,
                family = PromptLoader.ResolveFamily(candidateType).ToLowerInvariant(),
                subtype = a.ExtractedSubtype ?? candidateType,
                is_executed = a.ExtractedIsExecuted ?? false,
                risk_score = a.LegalMemoRiskScore,
                memo
            };
        }).ToList();

        var memosJson = JsonSerializer.Serialize(memos, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var template = PromptLoader.LoadCounselBrief();
        var prompt = PromptLoader.Render(template, new Dictionary<string, string>
        {
            ["DOCUMENT_MEMOS"] = memosJson
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await llm.CompleteAsync(new LanguageModelRequest
            {
                System = SystemPromptCounselBrief,
                Messages = [new LanguageModelMessage(LanguageModelRoles.User, prompt)],
                MaxTokens = CounselBriefMaxTokens,
                Temperature = CounselBriefTemperature,
                Purpose = "counsel-brief"
            }, cancellationToken);
            sw.Stop();

            var briefMd = response.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(briefMd))
            {
                return TypedResults.BadRequest(new ProblemSummary("empty_response",
                    "Counsel's brief returned empty."));
            }

            var existing = await db.CounselBriefs
                .FirstOrDefaultAsync(b => b.TenantId == userContext.TenantId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                existing = new CounselBrief
                {
                    TenantId = userContext.TenantId,
                    CreatedAt = now
                };
                db.CounselBriefs.Add(existing);
            }
            existing.BriefMd = briefMd;
            existing.Model = response.Model;
            existing.TokensIn = response.TokensIn;
            existing.TokensOut = response.TokensOut;
            existing.SourceDocCount = memos.Count;
            existing.LatencyMs = (int)sw.ElapsedMilliseconds;
            existing.GeneratedAt = now;
            existing.UpdatedAt = now;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "legal_advisor.counsel_brief.generate",
                ResourceType = "tenant",
                ResourceId = userContext.TenantId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    model = response.Model,
                    sourceDocCount = memos.Count,
                    tokensIn = response.TokensIn,
                    tokensOut = response.TokensOut,
                    latencyMs = sw.ElapsedMilliseconds
                }),
                CreatedAt = now
            });

            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new CounselBriefResponse(
                BriefMd: briefMd,
                Model: response.Model,
                SourceDocCount: memos.Count,
                TokensIn: response.TokensIn,
                TokensOut: response.TokensOut,
                LatencyMs: sw.ElapsedMilliseconds,
                GeneratedAt: now,
                Disclaimer: Disclaimer));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Counsel's brief generation failed");
            return TypedResults.BadRequest(new ProblemSummary("counsel_brief_failed", ex.Message));
        }
    }

    private static async Task<Results<Ok<CounselBriefResponse>, NotFound>> GetCounselBrief(
        HttpContext httpContext,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var brief = await db.CounselBriefs
            .FirstOrDefaultAsync(b => b.TenantId == userContext.TenantId, cancellationToken);
        if (brief is null) return TypedResults.NotFound();
        httpContext.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return TypedResults.Ok(new CounselBriefResponse(
            BriefMd: brief.BriefMd,
            Model: brief.Model,
            SourceDocCount: brief.SourceDocCount,
            TokensIn: brief.TokensIn ?? 0,
            TokensOut: brief.TokensOut ?? 0,
            LatencyMs: brief.LatencyMs ?? 0,
            GeneratedAt: brief.GeneratedAt,
            Disclaimer: Disclaimer));
    }

    // ------------------------------------------------------------------
    // Two-stage memo runner (markdown then JSON)
    // ------------------------------------------------------------------

    private static async Task<(int TokensIn, int TokensOut)> RunMemoTwoStageAsync(
        DocumentAsset asset,
        string candidateType,
        string fileName,
        string fullText,
        IDocumentLanguageModel llm,
        CancellationToken cancellationToken)
    {
        var truncated = fullText.Length > MaxInputChars;
        var docText = truncated ? fullText[..MaxInputChars] : fullText;

        // Pull headline JSON from the existing Stage-2 LLM extract — gives
        // the memo grounded parties/dates/$ figures without re-extracting.
        string headlineJson = "{}";
        if (!string.IsNullOrEmpty(asset.LlmExtractedFieldsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.LlmExtractedFieldsJson);
                if (doc.RootElement.TryGetProperty("headline", out var hl))
                {
                    headlineJson = hl.GetRawText();
                }
            }
            catch { /* leave default */ }
        }

        var familyOverlay = PromptLoader.LoadLegalMemoFamilyOverlay(candidateType);
        var masterTemplate = PromptLoader.LoadLegalMemoMaster();
        var stageAPrompt = PromptLoader.Render(masterTemplate, new Dictionary<string, string>
        {
            ["FILE_NAME"] = fileName,
            ["CANDIDATE_TYPE"] = candidateType,
            ["LAYOUT_PROVIDER"] = asset.LayoutProvider ?? "local_text",
            ["IS_EXECUTED"] = (asset.ExtractedIsExecuted ?? false) ? "Yes" : "No",
            ["NARRATIVE_BRIEF"] = asset.LlmNarrativeMd ?? "(no Document Intelligence Brief available — produce the memo from the source text alone)",
            ["HEADLINE_JSON"] = headlineJson,
            ["FAMILY_OVERLAY"] = familyOverlay,
            ["FULL_TEXT"] = docText
        });

        // ---- Stage A: markdown memo ----
        asset.LegalMemoStatus = "running";
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        var stageASw = System.Diagnostics.Stopwatch.StartNew();
        var stageAResp = await llm.CompleteAsync(new LanguageModelRequest
        {
            System = SystemPromptStageA,
            Messages = [new LanguageModelMessage(LanguageModelRoles.User, stageAPrompt)],
            MaxTokens = StageAMaxTokens,
            Temperature = StageATemperature,
            Purpose = $"legal-memo:{PromptLoader.ResolveFamily(candidateType)}"
        }, cancellationToken);
        stageASw.Stop();

        var memoMd = stageAResp.Text?.Trim();
        if (string.IsNullOrWhiteSpace(memoMd))
        {
            asset.LegalMemoStatus = "failed";
            throw new InvalidOperationException("Stage A returned an empty memo.");
        }

        asset.LegalMemoMd = memoMd;
        asset.LegalMemoModel = stageAResp.Model;
        asset.LegalMemoTokensIn = stageAResp.TokensIn;
        asset.LegalMemoTokensOut = stageAResp.TokensOut;
        asset.LegalMemoExtractedAt = DateTimeOffset.UtcNow;
        asset.LegalMemoLatencyMs = (int)stageASw.ElapsedMilliseconds;

        // ---- Stage B: JSON extraction from memo ----
        var stageBTpl = PromptLoader.LoadLegalMemoJson();
        var stageBPrompt = PromptLoader.Render(stageBTpl, new Dictionary<string, string>
        {
            ["LEGAL_MEMO"] = memoMd
        });

        var stageBResp = await llm.CompleteAsync(new LanguageModelRequest
        {
            System = SystemPromptStageB,
            Messages = [new LanguageModelMessage(LanguageModelRoles.User, stageBPrompt)],
            MaxTokens = StageBMaxTokens,
            Temperature = StageBTemperature,
            JsonSchema = "{}",
            Purpose = "legal-memo-extract"
        }, cancellationToken);

        var json = ExtractJson(stageBResp.Text);
        if (string.IsNullOrEmpty(json))
        {
            asset.LegalMemoStatus = "partial";  // memo md saved, JSON failed — UI can still render the markdown
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            return (stageAResp.TokensIn + stageBResp.TokensIn,
                    stageAResp.TokensOut + stageBResp.TokensOut);
        }

        asset.LegalMemoJson = json;
        asset.LegalMemoTokensIn = (asset.LegalMemoTokensIn ?? 0) + stageBResp.TokensIn;
        asset.LegalMemoTokensOut = (asset.LegalMemoTokensOut ?? 0) + stageBResp.TokensOut;
        asset.LegalMemoRiskScore = ParseRiskScore(json);
        asset.LegalMemoStatus = "completed";
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        return (stageAResp.TokensIn + stageBResp.TokensIn,
                stageAResp.TokensOut + stageBResp.TokensOut);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private const string Disclaimer =
        "AI-generated legal analysis for informational purposes only. " +
        "This is not legal advice and does not establish an attorney-client " +
        "relationship. Engage licensed counsel before relying on any " +
        "conclusion or taking action based on this output.";

    private static async Task<string> ResolveFileName(
        PracticeXDbContext db, DocumentAsset asset, CancellationToken cancellationToken)
    {
        if (!asset.SourceObjectId.HasValue) return "(unnamed)";
        var name = await db.SourceObjects
            .Where(s => s.Id == asset.SourceObjectId.Value)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name!;
    }

    private static string? ResolveDocumentText(DocumentAsset asset)
    {
        if (!string.IsNullOrEmpty(asset.LayoutJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.LayoutJson);
                if (doc.RootElement.TryGetProperty("fullText", out var ft))
                {
                    var s = ft.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch { /* swallow */ }
        }
        return asset.ExtractedFullText;
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return null;
    }

    private static decimal? ParseRiskScore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("risk_score", out var rs))
            {
                if (rs.ValueKind == JsonValueKind.Number && rs.TryGetDecimal(out var d)) return d;
                if (rs.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(rs.GetString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var s))
                    return s;
            }
        }
        catch { /* swallow */ }
        return null;
    }

    private static (string? Rating, string? Headline, string? TopIssue) ParseMemoSummary(string? memoJson)
    {
        if (string.IsNullOrEmpty(memoJson)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(memoJson);
            var root = doc.RootElement;
            string? rating = root.TryGetProperty("risk_rating", out var rr) && rr.ValueKind == JsonValueKind.String
                ? rr.GetString() : null;
            string? headline = root.TryGetProperty("headline", out var hl) && hl.ValueKind == JsonValueKind.String
                ? hl.GetString() : null;
            string? topIssue = null;
            if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
            {
                var first = issues.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    topIssue = t.GetString();
                }
            }
            return (rating, headline, topIssue);
        }
        catch { return (null, null, null); }
    }
}

// ----------------------------------------------------------------------------
// Response DTOs
// ----------------------------------------------------------------------------

public sealed record LegalMemoResult(
    string Status,
    string Model,
    decimal? RiskScore,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    string MemoMd,
    string MemoJson);

public sealed record LegalAdvisorPortfolioResponse(
    LegalAdvisorPortfolioCounts Counts,
    IReadOnlyList<LegalAdvisorPortfolioRow> Rows,
    string Disclaimer);

public sealed record LegalAdvisorPortfolioCounts(
    int Total,
    int WithMemo,
    int Severe,
    int High,
    int Elevated,
    int Modest,
    int Low);

public sealed record LegalAdvisorPortfolioRow(
    Guid DocumentAssetId,
    string FileName,
    string CandidateType,
    string Family,
    bool? IsExecuted,
    string? MemoStatus,
    decimal? RiskScore,
    string? RiskRating,
    string? Headline,
    string? TopIssueTitle,
    string? MemoModel,
    DateTimeOffset? MemoExtractedAt);

public sealed record CounselBriefResponse(
    string BriefMd,
    string? Model,
    int SourceDocCount,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    DateTimeOffset GeneratedAt,
    string Disclaimer);

internal sealed class LegalAdvisorMarker { }
