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
/// Slice 16: two-stage LLM pipeline.
///
/// Stage 1 (narrative). Family-specific prompt at temperature 0.3 puts the
/// model in a healthcare-attorney persona and produces a markdown
/// "Document Intelligence Brief" — sectioned, expansive, grounded in the
/// source. Saved to <c>document_assets.llm_narrative_md</c>.
///
/// Stage 2 (extraction). Family-specific prompt at temperature 0.1 reads
/// the brief as ground truth and emits a strict JSON object matching the
/// family schema (renewal cues, risk flags, compliance posture, etc.).
/// Saved to <c>document_assets.llm_extracted_fields_json</c> — same column
/// the v1 single-pass output used, so existing readers keep working.
///
/// Both stages are wrapped in one batch endpoint that re-runs the corpus.
/// </summary>
public static class LlmExtractionEndpoint
{
    public static IEndpointRouteBuilder MapLlmExtractionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapPost("/documents/{assetId:guid}/llm-extract", LlmExtract)
            .WithName("LlmExtract");
        group.MapPost("/llm-extract-batch", LlmExtractBatch)
            .WithName("LlmExtractBatch");
        return routes;
    }

    // Input cap: Sonnet 4.6 has a 200K context. We cap at 80K chars
    // (~20K tokens) so the prompt template + brief response still fit
    // comfortably. Most contracts (incl. physician employment with
    // Schedule attachments) live under 80K; large master leases may still
    // truncate, but they truncate after the meaningful sections.
    private const int MaxInputChars = 80_000;
    // Output cap: a 14-section lease brief routinely produces 16K-20K
    // chars (~5K-6K tokens). 8192 gives us headroom without runaway cost.
    private const int Stage1MaxTokens = 8192;
    private const int Stage2MaxTokens = 6144;
    private const double Stage1Temperature = 0.3;
    private const double Stage2Temperature = 0.1;

    private const string SystemPromptStage1 = """
        You are a senior healthcare-transactions attorney producing a Document
        Intelligence Brief. Follow the structural template in the user message
        exactly. Do not output JSON or code fences. Stay grounded in the
        source document; use sanctioned hedges instead of guessing.
        """;

    private const string SystemPromptStage2 = """
        You are a precise JSON extractor. Read the Document Intelligence
        Brief in the user message and emit a single JSON object that matches
        the schema. The brief is ground truth — do not infer beyond it. No
        prose, no markdown, no code fences — JSON only.
        """;

    private static async Task<Ok<BatchExtractionResult>> LlmExtractBatch(
        bool? force,
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<Marker> logger,
        CancellationToken cancellationToken)
    {
        if (!llm.IsConfigured)
        {
            return TypedResults.Ok(new BatchExtractionResult(
                Total: 0, Refined: 0, Skipped: 0, Failed: 0, TotalTokensIn: 0, TotalTokensOut: 0,
                LatencyMs: 0,
                Notes: "LLM not configured."));
        }

        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId)
            .ToListAsync(cancellationToken);

        var sourceNameById = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int refined = 0, skipped = 0, failed = 0;
        int totalIn = 0, totalOut = 0;
        var doForce = force == true;

        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip if both stages already done unless force=true.
            if (!doForce
                && !string.IsNullOrEmpty(asset.LlmNarrativeMd)
                && !string.IsNullOrEmpty(asset.LlmExtractedFieldsJson))
            {
                skipped++;
                continue;
            }

            var candidate = await db.DocumentCandidates
                .FirstOrDefaultAsync(c => c.DocumentAssetId == asset.Id && c.TenantId == userContext.TenantId, cancellationToken);
            var candidateType = candidate?.CandidateType ?? DocumentCandidateTypes.Unknown;

            var docText = ResolveDocumentText(asset);
            if (string.IsNullOrWhiteSpace(docText))
            {
                skipped++;
                continue;
            }

            try
            {
                var fileName = (asset.SourceObjectId.HasValue
                    && sourceNameById.TryGetValue(asset.SourceObjectId.Value, out var fn))
                    ? fn : "(unnamed)";

                var (tIn, tOut) = await RunTwoStageAsync(
                    asset, candidateType, fileName, docText, llm, cancellationToken);

                totalIn += tIn;
                totalOut += tOut;
                refined++;

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Two-stage extraction failed on asset {AssetId}", asset.Id);
                failed++;
                // Persist the failure status but never let a poisoned tracker
                // (e.g. unparseable jsonb that Postgres rejected) kill the
                // remaining batch — clear the tracker if the failure-save
                // itself throws so the next iteration starts clean.
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception saveEx)
                {
                    logger.LogWarning(saveEx,
                        "Failed to persist failure status for asset {AssetId}; clearing change tracker",
                        asset.Id);
                    db.ChangeTracker.Clear();
                }
            }
        }

        sw.Stop();

        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = userContext.TenantId,
            ActorType = "user",
            ActorId = userContext.UserId,
            EventType = "ingestion.llm.batch_two_stage",
            ResourceType = "tenant",
            ResourceId = userContext.TenantId,
            MetadataJson = JsonSerializer.Serialize(new
            {
                total = assets.Count,
                refined,
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
            Refined: refined,
            Skipped: skipped,
            Failed: failed,
            TotalTokensIn: totalIn,
            TotalTokensOut: totalOut,
            LatencyMs: sw.ElapsedMilliseconds,
            Notes: null));
    }

    private static async Task<Results<Ok<LlmExtractionResult>, NotFound, BadRequest<ProblemSummary>>> LlmExtract(
        Guid assetId,
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<Marker> logger,
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
        var candidateType = candidate?.CandidateType ?? DocumentCandidateTypes.Unknown;

        var docText = ResolveDocumentText(asset);
        if (string.IsNullOrWhiteSpace(docText))
        {
            return TypedResults.BadRequest(new ProblemSummary("no_text",
                "No extracted text available for this document. Re-ingest first."));
        }

        var fileName = "(unnamed)";
        if (asset.SourceObjectId.HasValue)
        {
            var src = await db.SourceObjects
                .Where(s => s.Id == asset.SourceObjectId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(src)) fileName = src!;
        }

        try
        {
            var (tIn, tOut) = await RunTwoStageAsync(
                asset, candidateType, fileName, docText, llm, cancellationToken);

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "ingestion.llm.two_stage",
                ResourceType = "document_asset",
                ResourceId = asset.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    candidateType,
                    family = PromptLoader.ResolveFamily(candidateType),
                    narrativeModel = asset.LlmNarrativeModel,
                    extractionModel = asset.LlmExtractorModel,
                    tokensIn = tIn,
                    tokensOut = tOut,
                    inputChars = docText.Length
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new LlmExtractionResult(
                Status: "completed",
                Model: asset.LlmExtractorModel ?? "",
                TokensIn: tIn,
                TokensOut: tOut,
                LatencyMs: (asset.LlmNarrativeLatencyMs ?? 0),
                Json: asset.LlmExtractedFieldsJson ?? "{}"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Two-stage LLM extraction failed for asset {AssetId}", assetId);
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemSummary("llm_failed", ex.Message));
        }
    }

    /// <summary>
    /// Drives stage 1 (narrative) then stage 2 (extraction) on a single asset.
    /// Persists both outputs onto the asset and returns token totals.
    /// Caller is responsible for SaveChangesAsync and audit-event emission.
    /// </summary>
    private static async Task<(int TokensIn, int TokensOut)> RunTwoStageAsync(
        DocumentAsset asset,
        string candidateType,
        string fileName,
        string fullText,
        IDocumentLanguageModel llm,
        CancellationToken cancellationToken)
    {
        var truncated = fullText.Length > MaxInputChars;
        var docText = truncated ? fullText[..MaxInputChars] : fullText;
        var family = PromptLoader.ResolveFamily(candidateType);
        var layoutProvider = asset.LayoutProvider ?? "local_text";

        // ---- Stage 1: narrative brief ----
        asset.LlmNarrativeStatus = "running";
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        var stage1Tpl = PromptLoader.LoadStage1(candidateType);
        var stage1Prompt = PromptLoader.Render(stage1Tpl, new Dictionary<string, string>
        {
            ["FILE_NAME"] = fileName,
            ["LAYOUT_PROVIDER"] = layoutProvider,
            ["CANDIDATE_TYPE"] = candidateType,
            ["PARENT_LEASE_HINT"] = "(no parent context provided)",
            ["FULL_TEXT"] = docText
        });

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var stage1Resp = await llm.CompleteAsync(new LanguageModelRequest
        {
            System = SystemPromptStage1,
            Messages = [new LanguageModelMessage(LanguageModelRoles.User, stage1Prompt)],
            MaxTokens = Stage1MaxTokens,
            Temperature = Stage1Temperature,
            Purpose = $"narrative:{family}"
        }, cancellationToken);
        stage1Sw.Stop();

        var brief = stage1Resp.Text?.Trim();
        if (string.IsNullOrWhiteSpace(brief))
        {
            asset.LlmNarrativeStatus = "failed";
            throw new InvalidOperationException("Stage 1 returned an empty narrative.");
        }

        asset.LlmNarrativeMd = brief;
        asset.LlmNarrativeModel = stage1Resp.Model;
        asset.LlmNarrativeTokensIn = stage1Resp.TokensIn;
        asset.LlmNarrativeTokensOut = stage1Resp.TokensOut;
        asset.LlmNarrativeExtractedAt = DateTimeOffset.UtcNow;
        asset.LlmNarrativeTemperature = (decimal)Stage1Temperature;
        asset.LlmNarrativeLatencyMs = (int)stage1Sw.ElapsedMilliseconds;
        asset.LlmNarrativeStatus = "completed";

        // ---- Stage 2: JSON extraction from brief ----
        asset.LlmExtractionStatus = "running";

        var stage2Tpl = PromptLoader.LoadStage2(candidateType);
        var stage2Prompt = PromptLoader.Render(stage2Tpl, new Dictionary<string, string>
        {
            ["NARRATIVE_BRIEF"] = brief
        });

        var stage2Resp = await llm.CompleteAsync(new LanguageModelRequest
        {
            System = SystemPromptStage2,
            Messages = [new LanguageModelMessage(LanguageModelRoles.User, stage2Prompt)],
            MaxTokens = Stage2MaxTokens,
            Temperature = Stage2Temperature,
            JsonSchema = "{}",
            Purpose = $"extract:{family}"
        }, cancellationToken);

        var json = ExtractJson(stage2Resp.Text);
        if (string.IsNullOrEmpty(json) || !IsParseableJson(json))
        {
            asset.LlmExtractionStatus = "failed";
            asset.LlmExtractedFieldsJson = null;
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            throw new InvalidOperationException("Stage 2 response did not contain a parseable JSON object.");
        }

        asset.LlmExtractedFieldsJson = json;
        asset.LlmExtractorModel = stage2Resp.Model;
        asset.LlmTokensIn = stage2Resp.TokensIn;
        asset.LlmTokensOut = stage2Resp.TokensOut;
        asset.LlmExtractedAt = DateTimeOffset.UtcNow;
        asset.LlmExtractionStatus = "completed";
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        var totalIn = stage1Resp.TokensIn + stage2Resp.TokensIn;
        var totalOut = stage1Resp.TokensOut + stage2Resp.TokensOut;
        return (totalIn, totalOut);
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

    private static bool IsParseableJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls the first {...} JSON object out of a model response. Models
    /// occasionally wrap JSON in code fences; this trims to the outermost
    /// balanced braces.
    /// </summary>
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
}

public sealed record LlmExtractionResult(
    string Status,
    string Model,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    string Json);

public sealed record BatchExtractionResult(
    int Total,
    int Refined,
    int Skipped,
    int Failed,
    int TotalTokensIn,
    int TotalTokensOut,
    long LatencyMs,
    string? Notes);

public sealed record ProblemSummary(string Code, string Detail);

internal sealed class Marker { }
