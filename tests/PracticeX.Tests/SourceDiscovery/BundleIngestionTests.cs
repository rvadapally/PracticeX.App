using System.Text;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.SourceDiscovery.Connectors;
using PracticeX.Application.SourceDiscovery.Ingestion;
using PracticeX.Domain.Documents;
using PracticeX.Domain.Sources;
using PracticeX.Infrastructure.SourceDiscovery.Ingestion;
using PracticeX.Tests.SourceDiscovery.Support;

namespace PracticeX.Tests.SourceDiscovery;

public class BundleIngestionTests
{
    [Fact]
    public async Task IngestBundleAsync_PromotesManifestBatchToComplete_PopulatesValidityColumns()
    {
        using var fx = new OrchestratorFixture();

        var manifestItem = new ManifestItem
        {
            RelativePath = "Payers/BCBS/Amendment terms.txt",
            Name = "Amendment terms.txt",
            SizeBytes = 200,
            LastModifiedUtc = fx.Clock.UtcNow,
            MimeType = "text/plain"
        };

        var manifest = (await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), [manifestItem], CancellationToken.None)).Value!;

        // Replay the same path with bytes via the bundle endpoint.
        var bytes = Encoding.UTF8.GetBytes("Plain text contract body so the validity inspector routes to local_text.");
        var manifestExternalId = IngestionOrchestrator.BuildManifestExternalId(manifestItem);
        var discovery = new DiscoveryResult
        {
            Items = new List<DiscoveredItem>
            {
                new()
                {
                    ExternalId = manifestExternalId,
                    Name = manifestItem.Name,
                    MimeType = "text/plain",
                    RelativePath = manifestItem.RelativePath,
                    SizeBytes = bytes.LongLength,
                    InlineContent = bytes
                }
            },
            Notes = []
        };

        var result = await fx.Orchestrator.IngestBundleAsync(NewRequest(fx), manifest.BatchId, discovery, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var batch = await fx.Db.IngestionBatches.SingleAsync(b => b.Id == manifest.BatchId);
        Assert.Equal(IngestionBatchPhase.Complete, batch.Phase);
        Assert.Contains(batch.Status, new[] { IngestionBatchStatus.Completed, IngestionBatchStatus.PartialSuccess });

        var asset = await fx.Db.DocumentAssets.SingleAsync();
        Assert.NotNull(asset.ExtractionRoute);
        Assert.NotNull(asset.ValidityStatus);
        Assert.Equal(ExtractionRoutes.LocalText, asset.ExtractionRoute);

        var candidate = await fx.Db.DocumentCandidates.SingleAsync();
        Assert.Equal(asset.Id, candidate.DocumentAssetId);
    }

    [Fact]
    public async Task IngestBundleAsync_UnknownBatchId_ReturnsFailure()
    {
        using var fx = new OrchestratorFixture();
        var bogusBatch = Guid.NewGuid();

        var discovery = new DiscoveryResult { Items = [], Notes = [] };
        var result = await fx.Orchestrator.IngestBundleAsync(NewRequest(fx), bogusBatch, discovery, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("manifest_batch_not_found", result.Error?.Code);
    }

    [Fact]
    public async Task IngestBundleAsync_BatchAlreadyComplete_Refused()
    {
        using var fx = new OrchestratorFixture();
        var manifestItem = new ManifestItem
        {
            RelativePath = "x.pdf", Name = "x.pdf",
            SizeBytes = 100, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "application/pdf"
        };
        var manifest = (await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), [manifestItem], CancellationToken.None)).Value!;

        // First bundle promotes phase to complete.
        var firstDiscovery = new DiscoveryResult
        {
            Items = new List<DiscoveredItem>
            {
                new()
                {
                    ExternalId = IngestionOrchestrator.BuildManifestExternalId(manifestItem),
                    Name = "x.pdf", MimeType = "text/plain", RelativePath = "x.pdf",
                    SizeBytes = 4, InlineContent = "body"u8.ToArray()
                }
            },
            Notes = []
        };
        await fx.Orchestrator.IngestBundleAsync(NewRequest(fx), manifest.BatchId, firstDiscovery, CancellationToken.None);

        // Second attempt against the now-complete batch must be refused.
        var second = await fx.Orchestrator.IngestBundleAsync(NewRequest(fx), manifest.BatchId, firstDiscovery, CancellationToken.None);
        Assert.False(second.IsSuccess);
        Assert.Equal("manifest_already_complete", second.Error?.Code);
    }

    [Fact]
    public async Task IngestBundleAsync_EmitsBundleAuditEvents()
    {
        using var fx = new OrchestratorFixture();
        var manifestItem = new ManifestItem
        {
            RelativePath = "x.pdf", Name = "x.pdf",
            SizeBytes = 50, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "application/pdf"
        };
        var manifest = (await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), [manifestItem], CancellationToken.None)).Value!;

        var discovery = new DiscoveryResult
        {
            Items = new List<DiscoveredItem>
            {
                new()
                {
                    ExternalId = IngestionOrchestrator.BuildManifestExternalId(manifestItem),
                    Name = "x.pdf", MimeType = "text/plain", RelativePath = "x.pdf",
                    SizeBytes = 4, InlineContent = "body"u8.ToArray()
                }
            },
            Notes = []
        };
        await fx.Orchestrator.IngestBundleAsync(NewRequest(fx), manifest.BatchId, discovery, CancellationToken.None);

        var audit = await fx.Db.AuditEvents
            .Where(a => a.ResourceId == manifest.BatchId)
            .Select(a => a.EventType)
            .ToListAsync();

        Assert.Contains("ingestion.bundle.received", audit);
        Assert.Contains("ingestion.bundle.completed", audit);
    }

    private static IngestionRequest NewRequest(OrchestratorFixture fx) => new()
    {
        TenantId = fx.TenantId,
        InitiatedByUserId = fx.UserId,
        ConnectionId = fx.ConnectionId,
        SourceType = SourceTypes.LocalFolder
    };
}
