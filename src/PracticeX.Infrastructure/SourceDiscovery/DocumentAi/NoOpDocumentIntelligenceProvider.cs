using PracticeX.Discovery.DocumentAi;

namespace PracticeX.Infrastructure.SourceDiscovery.DocumentAi;

/// <summary>
/// Default implementation when DocumentIntelligence:Enabled=false or no
/// credentials are configured. Reports IsConfigured=false; throwing on use
/// surfaces accidental call paths during dev rather than silently failing.
/// </summary>
public sealed class NoOpDocumentIntelligenceProvider : IDocumentIntelligenceProvider
{
    public string Name => "noop";
    public bool IsConfigured => false;

    public Task<DocumentExtractionResult> ExtractLayoutAsync(
        DocumentExtractionRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Document Intelligence is not configured. " +
            "Set DocumentIntelligence:Enabled=true, populate Endpoint + ApiKey, " +
            "and add the tenant to AllowedTenantIds before calling.");

    public Task<DocumentExtractionResult> ExtractFieldsAsync(
        DocumentExtractionRequest request,
        string modelId,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Document Intelligence is not configured. " +
            "Set DocumentIntelligence:Enabled=true, populate Endpoint + ApiKey, " +
            "and add the tenant to AllowedTenantIds before calling.");
}
