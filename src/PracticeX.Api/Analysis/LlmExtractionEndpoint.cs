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
/// Slice 13: LLM-refined extraction. POST /api/analysis/documents/{id}/llm-extract
/// loads the asset's text (Doc Intel layout fullText, falling back to local
/// extraction), assembles a family-specific prompt, calls the configured
/// IDocumentLanguageModel, parses the JSON response, and stores it in
/// document_assets.llm_extracted_fields_json. UI shows LLM fields when
/// present and falls back to regex output otherwise.
/// </summary>
public static class LlmExtractionEndpoint
{
    public static IEndpointRouteBuilder MapLlmExtractionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapPost("/documents/{assetId:guid}/llm-extract", LlmExtract)
            .WithName("LlmExtract");
        return routes;
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
            return TypedResults.BadRequest(new ProblemSummary("llm_not_configured", "LLM provider isn't configured. Set OpenRouter:Enabled=true and OpenRouter:ApiKey via user-secrets."));
        }

        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return TypedResults.NotFound();

        var candidate = await db.DocumentCandidates
            .FirstOrDefaultAsync(c => c.DocumentAssetId == assetId && c.TenantId == userContext.TenantId, cancellationToken);
        var candidateType = candidate?.CandidateType ?? DocumentCandidateTypes.Unknown;

        // Source the text. Prefer Doc Intel layout fullText, fall back to local-extracted.
        string? text = null;
        if (!string.IsNullOrEmpty(asset.LayoutJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(asset.LayoutJson);
                if (doc.RootElement.TryGetProperty("fullText", out var ft))
                {
                    text = ft.GetString();
                }
            }
            catch { /* swallow */ }
        }
        if (string.IsNullOrEmpty(text)) text = asset.ExtractedFullText;

        if (string.IsNullOrWhiteSpace(text))
        {
            return TypedResults.BadRequest(new ProblemSummary("no_text", "No extracted text available for this document. Re-ingest first."));
        }

        // Cap input at ~30k chars (~7k tokens) - Claude Haiku context is plenty
        // larger but we don't want a 200-page lease blowing through tokens.
        const int MaxChars = 30_000;
        var truncated = text.Length > MaxChars;
        var docText = truncated ? text[..MaxChars] : text;

        var prompt = BuildPrompt(candidateType, docText, truncated);

        asset.LlmExtractionStatus = "running";
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var response = await llm.CompleteAsync(new LanguageModelRequest
            {
                System = SystemPrompt,
                Messages = [new LanguageModelMessage(LanguageModelRoles.User, prompt)],
                MaxTokens = 2048,
                Temperature = 0.0,
                JsonSchema = "{}",
                Purpose = $"extract:{candidateType}"
            }, cancellationToken);

            var json = ExtractJson(response.Text);
            if (string.IsNullOrEmpty(json))
            {
                throw new InvalidOperationException("LLM response did not contain a parseable JSON object.");
            }

            // Store as-is (jsonb validates).
            asset.LlmExtractedFieldsJson = json;
            asset.LlmExtractorModel = response.Model;
            asset.LlmTokensIn = response.TokensIn;
            asset.LlmTokensOut = response.TokensOut;
            asset.LlmExtractedAt = DateTimeOffset.UtcNow;
            asset.LlmExtractionStatus = "completed";
            asset.UpdatedAt = DateTimeOffset.UtcNow;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "ingestion.llm.extracted",
                ResourceType = "document_asset",
                ResourceId = asset.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    model = response.Model,
                    tokensIn = response.TokensIn,
                    tokensOut = response.TokensOut,
                    latencyMs = response.LatencyMs,
                    candidateType,
                    inputChars = docText.Length,
                    truncated
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new LlmExtractionResult(
                Status: "completed",
                Model: response.Model,
                TokensIn: response.TokensIn,
                TokensOut: response.TokensOut,
                LatencyMs: response.LatencyMs,
                Json: json));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM extraction failed for asset {AssetId}", assetId);
            asset.LlmExtractionStatus = "failed";
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemSummary("llm_failed", ex.Message));
        }
    }

    /// <summary>
    /// Pulls the first {...} JSON object out of a model response. Models often
    /// wrap JSON in code fences or add a sentence before; this trims to the
    /// outermost balanced braces.
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

    private const string SystemPrompt = """
        You extract structured fields from healthcare-practice contracts. Always reply with a single JSON object that conforms to the schema in the user message. Never include commentary, code fences, or explanation - just JSON. When a field can't be confidently extracted, use null. Use ISO 8601 dates (YYYY-MM-DD). For party names, return the legal entity name only - strip "hereinafter known as", role labels, comma-trailing parentheticals.
        """;

    private static string BuildPrompt(string candidateType, string text, bool truncated) =>
        candidateType switch
        {
            "lease" or "lease_amendment" or "lease_loi" or "sublease" => LeasePrompt(text, truncated),
            "nda" => NdaPrompt(text, truncated),
            "employee_agreement" or "amendment" => EmploymentPrompt(text, truncated),
            "call_coverage_agreement" => CallCoveragePrompt(text, truncated),
            "bylaws" or "vendor_contract" or "service_agreement" or "processor_agreement" => GenericPrompt(candidateType, text, truncated),
            _ => GenericPrompt(candidateType, text, truncated)
        };

    private static string LeasePrompt(string text, bool truncated) => $$"""
        Extract these fields from the lease document below. Return ONLY a JSON object.

        Schema:
        {
          "subtype": "master_lease" | "lease_amendment" | "lease_loi" | "sublease" | null,
          "amendment_number": <integer or null>,
          "parent_agreement_date": "YYYY-MM-DD" | null,
          "effective_date": "YYYY-MM-DD" | null,
          "landlord": "<legal entity name>" | null,
          "tenant": "<legal entity name>" | null,
          "premises": [
            {
              "street_address": "<street + city + state>",
              "suite": "<suite number>",
              "rentable_square_feet": <number or null>
            }
          ],
          "rent": {
            "base_amount": <number or null>,
            "period": "month" | "year" | null,
            "currency": "USD",
            "escalation_pattern": "<short description or null>",
            "deferred": <boolean>
          },
          "term_months": <integer or null>,
          "governing_law": "<state or null>",
          "is_signed": <boolean>,
          "signers": [{ "name": "<full name>", "title": "<role>", "signed_date": "YYYY-MM-DD" | null }]
        }

        {{(truncated ? "Note: document was truncated; fields near the end may be incomplete.\n\n" : "")}}Document:
        ---
        {{text}}
        ---
        """;

    private static string NdaPrompt(string text, bool truncated) => $$"""
        Extract these fields from the NDA document below. Return ONLY a JSON object.

        Schema:
        {
          "subtype": "bilateral_individual" | "mutual_org" | "investor_template" | "advisor_template" | "demo_participant_template" | null,
          "effective_date": "YYYY-MM-DD" | null,
          "is_mutual": <boolean>,
          "is_template": <boolean>,
          "parties": [
            { "type": "person" | "organization", "name": "<legal name>", "role": "disclosing" | "receiving" | "both" | null }
          ],
          "permitted_purpose": "<short string or null>",
          "term_months": <integer or null>,
          "governing_law": "<state or null>",
          "signers": [{ "name": "<full name>", "title": "<role>", "signed_date": "YYYY-MM-DD" | null }]
        }

        {{(truncated ? "Note: document was truncated; fields near the end may be incomplete.\n\n" : "")}}Document:
        ---
        {{text}}
        ---
        """;

    private static string EmploymentPrompt(string text, bool truncated) => $$"""
        Extract these fields from the employment / shareholder document below. Return ONLY a JSON object.

        Schema:
        {
          "subtype": "offer_letter" | "engagement_letter" | "advisor_agreement" | "ciia" | "phi_agreement" | "physician_employment" | "shareholder_addendum" | null,
          "amendment_number": <integer or null>,
          "parent_agreement_date": "YYYY-MM-DD" | null,
          "effective_date": "YYYY-MM-DD" | null,
          "parties": [
            { "name": "<legal name>", "role": "employer" | "employee" | "physician" | "medical_group" | null, "title": "<job title or null>" }
          ],
          "compensation_summary": "<one-paragraph summary or null>",
          "equity_grants": [
            { "type": "core_advisory" | "growth" | "option" | "rsu" | null,
              "percentage": <number or null>, "shares": <integer or null>,
              "vesting_months": <integer or null>, "cliff_months": <integer or null> }
          ],
          "term_months": <integer or null>,
          "governing_law": "<state or null>",
          "is_signed": <boolean>,
          "signers": [{ "name": "<full name>", "title": "<role>", "signed_date": "YYYY-MM-DD" | null }]
        }

        {{(truncated ? "Note: document was truncated; fields near the end may be incomplete.\n\n" : "")}}Document:
        ---
        {{text}}
        ---
        """;

    private static string CallCoveragePrompt(string text, bool truncated) => $$"""
        Extract these fields from the call-coverage agreement below. Return ONLY a JSON object.

        Schema:
        {
          "effective_date": "YYYY-MM-DD" | null,
          "parties": [
            { "role": "medical_group" | "covering_physician" | "covered_facility", "name": "<legal name>", "specialty": "<specialty or null>" }
          ],
          "coverage_specialty": "<specialty>",
          "coverage_windows": [
            { "type": "24x7" | "weekend" | "weekday_evenings" | "holiday" | "after_hours" | null,
              "schedule_description": "<free-text description>" }
          ],
          "compensation": {
            "per_shift_amount": <number or null>,
            "per_day_amount": <number or null>,
            "per_month_amount": <number or null>,
            "currency": "USD"
          },
          "term_months": <integer or null>,
          "governing_law": "<state or null>",
          "is_signed": <boolean>,
          "signers": [{ "name": "<full name>", "title": "<role>", "signed_date": "YYYY-MM-DD" | null }]
        }

        {{(truncated ? "Note: document was truncated; fields near the end may be incomplete.\n\n" : "")}}Document:
        ---
        {{text}}
        ---
        """;

    private static string GenericPrompt(string candidateType, string text, bool truncated) => $$"""
        Extract these fields from the contract document below. The classifier flagged this as "{{candidateType}}". Return ONLY a JSON object.

        Schema:
        {
          "document_type": "<short description>",
          "effective_date": "YYYY-MM-DD" | null,
          "parties": [{ "name": "<legal name>", "role": "<role>" }],
          "key_terms": [{ "term": "<short label>", "description": "<one-sentence summary>" }],
          "term_months": <integer or null>,
          "governing_law": "<state or null>",
          "is_signed": <boolean>,
          "signers": [{ "name": "<full name>", "title": "<role>", "signed_date": "YYYY-MM-DD" | null }],
          "summary": "<one-paragraph plain-language summary>"
        }

        {{(truncated ? "Note: document was truncated; fields near the end may be incomplete.\n\n" : "")}}Document:
        ---
        {{text}}
        ---
        """;
}

public sealed record LlmExtractionResult(
    string Status,
    string Model,
    int TokensIn,
    int TokensOut,
    long LatencyMs,
    string Json);

public sealed record ProblemSummary(string Code, string Detail);

internal sealed class Marker { }
