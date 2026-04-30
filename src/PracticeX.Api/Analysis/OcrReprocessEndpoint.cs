using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.Common;
using PracticeX.Application.SourceDiscovery.Storage;
using PracticeX.Discovery.DocumentAi;
using PracticeX.Domain.Audit;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Api.Analysis;

/// <summary>
/// Manual re-OCR for assets where local-text extraction (PdfPig) returned
/// nothing. Pulls bytes from storage, sends them through
/// <see cref="IDocumentIntelligenceProvider"/>, and persists the extracted
/// layout + full text. Lets us salvage scanned PDFs that classifier-time
/// validity inspection misjudged as having a text layer.
/// </summary>
public static class OcrReprocessEndpoint
{
    public static IEndpointRouteBuilder MapOcrReprocessEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/analysis").WithTags("Analysis");
        group.MapPost("/documents/{assetId:guid}/reprocess-ocr", ReprocessOcr)
            .WithName("ReprocessOcr");
        return routes;
    }

    private static async Task<Results<Ok<OcrReprocessResult>, NotFound, BadRequest<ProblemSummary>>> ReprocessOcr(
        Guid assetId,
        PracticeXDbContext db,
        IDocumentStorage storage,
        IDocumentIntelligenceProvider docIntel,
        ICurrentUserContext userContext,
        ILogger<Marker> logger,
        CancellationToken cancellationToken)
    {
        if (!docIntel.IsConfigured)
        {
            return TypedResults.BadRequest(new ProblemSummary("docintel_not_configured",
                "Document Intelligence is not configured for this environment."));
        }

        var asset = await db.DocumentAssets
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == userContext.TenantId, cancellationToken);
        if (asset is null) return TypedResults.NotFound();

        // Read bytes from storage.
        byte[] bytes;
        try
        {
            using var stream = await storage.OpenReadAsync(asset.StorageUri, cancellationToken);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            bytes = ms.ToArray();
        }
        catch (FileNotFoundException)
        {
            return TypedResults.BadRequest(new ProblemSummary("storage_missing",
                $"Storage file not found at {asset.StorageUri}."));
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

        asset.OcrStatus = "running";
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var extraction = await docIntel.ExtractLayoutAsync(
                new DocumentExtractionRequest(
                    Content: bytes,
                    FileName: fileName,
                    MimeType: asset.MimeType,
                    MaxPages: 200),
                cancellationToken);

            asset.LayoutJson = JsonSerializer.Serialize(new
            {
                provider = extraction.ProviderName,
                model = extraction.ProviderModel,
                fullText = extraction.FullText,
                pages = extraction.Pages,
                tables = extraction.Tables,
                keyValuePairs = extraction.KeyValuePairs,
                latencyMs = extraction.LatencyMs
            });
            asset.LayoutProvider = extraction.ProviderName;
            asset.LayoutModel = extraction.ProviderModel;
            asset.LayoutExtractedAt = DateTimeOffset.UtcNow;
            asset.LayoutPageCount = extraction.Pages.Count;
            asset.OcrStatus = "completed";
            // Mirror full text onto extracted_full_text so downstream readers
            // (LLM extraction's ResolveDocumentText, regex extractor) get it
            // without having to parse layout_json.
            asset.ExtractedFullText = extraction.FullText.Length > 256_000
                ? extraction.FullText[..256_000]
                : extraction.FullText;
            asset.UpdatedAt = DateTimeOffset.UtcNow;

            db.AuditEvents.Add(new AuditEvent
            {
                TenantId = userContext.TenantId,
                ActorType = "user",
                ActorId = userContext.UserId,
                EventType = "ingestion.layout.reprocessed",
                ResourceType = "document_asset",
                ResourceId = asset.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    provider = extraction.ProviderName,
                    model = extraction.ProviderModel,
                    pageCount = extraction.Pages.Count,
                    textChars = extraction.FullText.Length,
                    latencyMs = extraction.LatencyMs
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);

            return TypedResults.Ok(new OcrReprocessResult(
                Status: "completed",
                Provider: extraction.ProviderName,
                Model: extraction.ProviderModel,
                PageCount: extraction.Pages.Count,
                TextChars: extraction.FullText.Length,
                LatencyMs: extraction.LatencyMs));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-OCR failed for asset {AssetId}", assetId);
            asset.OcrStatus = "failed";
            asset.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemSummary("ocr_failed", ex.Message));
        }
    }
}

public sealed record OcrReprocessResult(
    string Status,
    string Provider,
    string Model,
    int PageCount,
    int TextChars,
    long LatencyMs);
