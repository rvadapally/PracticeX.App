using PracticeX.Discovery.Classification;
using PracticeX.Discovery.Contracts;
using PracticeX.Discovery.Signatures;
using PracticeX.Discovery.Validation;
using PracticeX.Domain.Documents;

namespace PracticeX.Discovery.Pipelines;

/// <summary>
/// Default impl. Bytes-optional pipeline: classifier always runs; validity
/// inspector and signature detector run when bytes are supplied.
/// A detected signature boosts confidence by +0.30 (capped at 0.99), pushing
/// signed contracts into the Strong band even when the filename is opaque.
/// </summary>
public sealed class DefaultDocumentDiscoveryPipeline(
    IDocumentClassifier classifier,
    IDocumentValidityInspector? validityInspector = null,
    ISignatureDetector? signatureDetector = null) : IDocumentDiscoveryPipeline
{
    public const decimal SignatureConfidenceBoost = 0.30m;

    public Task<ManifestScoredItemDto> ScoreAsync(
        ManifestItemDto item,
        byte[]? content,
        CancellationToken cancellationToken)
    {
        var folderHint = ExtractFolderHint(item.RelativePath);
        var mimeType = item.MimeType ?? "application/octet-stream";

        var classification = classifier.Classify(new ClassificationInput
        {
            FileName = item.Name,
            RelativePath = item.RelativePath,
            MimeType = mimeType,
            SizeBytes = item.SizeBytes,
            FolderHint = folderHint,
            Hints = []
        });

        var reasonCodes = classification.ReasonCodes.ToList();
        var confidence = classification.Confidence;
        var hasSignature = false;
        var signatureCount = 0;
        IReadOnlyList<string> signatureProviders = Array.Empty<string>();

        if (content is not null && content.Length > 0)
        {
            if (validityInspector is not null)
            {
                var validity = validityInspector.Inspect(content, mimeType, item.Name);
                foreach (var rc in validity.ReasonCodes)
                {
                    reasonCodes.Add(rc);
                }
            }

            if (signatureDetector is not null && signatureDetector.CanInspect(mimeType, item.Name))
            {
                var sig = signatureDetector.Inspect(content, mimeType, item.Name);
                if (sig.HasSignature)
                {
                    hasSignature = true;
                    signatureCount = sig.SignatureCount;
                    signatureProviders = sig.Providers;
                    foreach (var rc in sig.ReasonCodes)
                    {
                        if (!reasonCodes.Contains(rc)) reasonCodes.Add(rc);
                    }
                    confidence = Math.Min(0.99m, confidence + SignatureConfidenceBoost);
                }
            }
        }

        var manifestItemId = BuildManifestItemId(item);
        var band = ManifestBands.From(confidence);
        var action = ManifestBands.RecommendedAction(confidence);

        var scored = new ManifestScoredItemDto(
            ManifestItemId: manifestItemId,
            RelativePath: item.RelativePath,
            Name: item.Name,
            SizeBytes: item.SizeBytes,
            CandidateType: classification.CandidateType,
            Confidence: decimal.Round(confidence, 4),
            ReasonCodes: reasonCodes,
            RecommendedAction: action,
            Band: band,
            CounterpartyHint: classification.CounterpartyHint,
            HasSignature: hasSignature,
            SignatureCount: signatureCount,
            SignatureProviders: signatureProviders
        );

        return Task.FromResult(scored);
    }

    public static string BuildManifestItemId(ManifestItemDto item) =>
        $"manifest:{item.RelativePath}|{item.SizeBytes}|{item.LastModifiedUtc.ToUnixTimeSeconds()}";

    private static string? ExtractFolderHint(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return null;
        }
        var normalized = relativePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? null : normalized[..lastSlash];
    }
}

