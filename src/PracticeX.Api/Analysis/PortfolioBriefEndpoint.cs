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
/// Slice 16.6 — stage 3: Practice Intelligence Brief.
/// Synthesizes the per-document stage-2 outputs across the whole tenant
/// into one executive markdown brief, persisted in doc.portfolio_briefs.
/// One row per tenant; regenerate replaces it.
/// </summary>
public static class PortfolioBriefEndpoint
{
    public static IEndpointRouteBuilder MapPortfolioBriefEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapGet("/portfolio-brief", GetPortfolioBrief).WithName("GetPortfolioBrief");
        group.MapPost("/portfolio-brief", GeneratePortfolioBrief).WithName("GeneratePortfolioBrief");
        return routes;
    }

    private const int Stage3MaxTokens = 8192;
    private const double Stage3Temperature = 0.3;

    private const string SystemPromptStage3 = """
        You are a senior healthcare-practice strategic advisor producing a
        Practice Intelligence Brief for the managing partners. Follow the
        9-section template in the user message exactly. Markdown output;
        no JSON, no code fences. Synthesize across all the per-document
        cards; cite source documents in parentheses where claims need
        backup. Stay grounded — do not invent facts not present in any card.
        """;

    private static async Task<Results<Ok<PortfolioBriefResponse>, NotFound>> GetPortfolioBrief(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var brief = await db.PortfolioBriefs
            .FirstOrDefaultAsync(b => b.TenantId == userContext.TenantId, cancellationToken);
        if (brief is null) return TypedResults.NotFound();
        return TypedResults.Ok(new PortfolioBriefResponse(
            BriefMd: brief.BriefMd,
            Model: brief.Model,
            SourceDocCount: brief.SourceDocCount,
            TokensIn: brief.TokensIn ?? 0,
            TokensOut: brief.TokensOut ?? 0,
            LatencyMs: brief.LatencyMs ?? 0,
            GeneratedAt: brief.GeneratedAt));
    }

    private static async Task<Results<Ok<PortfolioBriefResponse>, BadRequest<ProblemSummary>>> GeneratePortfolioBrief(
        PracticeXDbContext db,
        IDocumentLanguageModel llm,
        ICurrentUserContext userContext,
        ILogger<Marker> logger,
        CancellationToken cancellationToken)
    {
        if (!llm.IsConfigured)
        {
            return TypedResults.BadRequest(new ProblemSummary("llm_not_configured",
                "LLM provider isn't configured."));
        }

        // Gather per-doc stage-2 cards. Skip docs without llm_extracted_fields_json.
        var assets = await db.DocumentAssets
            .Where(a => a.TenantId == userContext.TenantId &&
                        a.LlmExtractedFieldsJson != null)
            .ToListAsync(cancellationToken);

        if (assets.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemSummary("no_briefs",
                "No per-document briefs available yet. Run /llm-extract-batch first."));
        }

        var sourceNames = await db.SourceObjects
            .Where(s => s.TenantId == userContext.TenantId)
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var candidatesByAsset = await db.DocumentCandidates
            .Where(c => c.TenantId == userContext.TenantId)
            .ToDictionaryAsync(c => c.DocumentAssetId, c => c.CandidateType, cancellationToken);

        var cards = assets.Select(a =>
        {
            var fileName = (a.SourceObjectId.HasValue && sourceNames.TryGetValue(a.SourceObjectId.Value, out var n))
                ? n : "(unnamed)";
            var candidateType = candidatesByAsset.GetValueOrDefault(a.Id, "unknown");
            var family = PromptLoader.ResolveFamily(candidateType).ToLowerInvariant();

            JsonElement? extracted = null;
            try
            {
                using var doc = JsonDocument.Parse(a.LlmExtractedFieldsJson!);
                extracted = doc.RootElement.Clone();
            }
            catch { /* leave null */ }

            string? plainEnglish = null;
            if (extracted is JsonElement el && el.TryGetProperty("plain_english_summary", out var pes))
            {
                plainEnglish = pes.ValueKind == JsonValueKind.String ? pes.GetString() : null;
            }

            return new
            {
                file_name = fileName,
                family,
                subtype = a.ExtractedSubtype ?? candidateType,
                extracted = extracted,
                plain_english_summary = plainEnglish
            };
        }).ToList();

        var cardsJson = JsonSerializer.Serialize(cards, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var template = PromptLoader.LoadStage3();
        var prompt = PromptLoader.Render(template, new Dictionary<string, string>
        {
            ["DOCUMENT_CARDS"] = cardsJson
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await llm.CompleteAsync(new LanguageModelRequest
            {
                System = SystemPromptStage3,
                Messages = [new LanguageModelMessage(LanguageModelRoles.User, prompt)],
                MaxTokens = Stage3MaxTokens,
                Temperature = Stage3Temperature,
                Purpose = "portfolio-brief"
            }, cancellationToken);
            sw.Stop();

            var briefMd = response.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(briefMd))
            {
                return TypedResults.BadRequest(new ProblemSummary("empty_response",
                    "Stage 3 returned an empty brief."));
            }

            // Upsert.
            var existing = await db.PortfolioBriefs
                .FirstOrDefaultAsync(b => b.TenantId == userContext.TenantId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                existing = new PortfolioBrief
                {
                    TenantId = userContext.TenantId,
                    CreatedAt = now
                };
                db.PortfolioBriefs.Add(existing);
            }
            existing.BriefMd = briefMd;
            existing.Model = response.Model;
            existing.TokensIn = response.TokensIn;
            existing.TokensOut = response.TokensOut;
            existing.SourceDocCount = cards.Count;
            existing.LatencyMs = (int)sw.ElapsedMilliseconds;
            existing.GeneratedAt = now;
            existing.UpdatedAt = now;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "ingestion.llm.portfolio_brief",
                ResourceType = "tenant",
                ResourceId = userContext.TenantId,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    model = response.Model,
                    sourceDocCount = cards.Count,
                    tokensIn = response.TokensIn,
                    tokensOut = response.TokensOut,
                    latencyMs = sw.ElapsedMilliseconds
                }),
                CreatedAt = now
            });

            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new PortfolioBriefResponse(
                BriefMd: briefMd,
                Model: response.Model,
                SourceDocCount: cards.Count,
                TokensIn: response.TokensIn,
                TokensOut: response.TokensOut,
                LatencyMs: sw.ElapsedMilliseconds,
                GeneratedAt: now));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stage 3 portfolio brief generation failed");
            return TypedResults.BadRequest(new ProblemSummary("stage3_failed", ex.Message));
        }
    }
}

public sealed record PortfolioBriefResponse(
    string BriefMd,
    string? Model,
    int SourceDocCount,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    DateTimeOffset GeneratedAt);
