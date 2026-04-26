using Microsoft.EntityFrameworkCore;
using PracticeX.Application.SourceDiscovery.Ingestion;
using PracticeX.Domain.Documents;
using PracticeX.Domain.Sources;
using PracticeX.Tests.SourceDiscovery.Support;

namespace PracticeX.Tests.SourceDiscovery;

public class ManifestScanTests
{
    [Fact]
    public async Task ScoreManifestAsync_PersistsBatchInManifestPhase_NoAssetsCreated()
    {
        using var fx = new OrchestratorFixture();

        var items = new List<ManifestItem>
        {
            new() { RelativePath = "Payers/BCBS/2024 Amendment 3.pdf", Name = "2024 Amendment 3.pdf",
                    SizeBytes = 12_000, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "application/pdf" },
            new() { RelativePath = "Marketing/Logo.png", Name = "Logo.png",
                    SizeBytes = 8_000, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "image/png" }
        };

        var result = await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), items, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var scan = result.Value!;

        var batch = await fx.Db.IngestionBatches.SingleAsync(b => b.Id == scan.BatchId);
        Assert.Equal(IngestionBatchPhase.Manifest, batch.Phase);
        Assert.Equal(2, batch.FileCount);

        var sourceObjects = await fx.Db.SourceObjects.Where(s => s.ConnectionId == fx.ConnectionId).ToListAsync();
        Assert.Equal(2, sourceObjects.Count);
        Assert.All(sourceObjects, s => Assert.Equal(SourceObjectProposedStatuses.Proposed, s.ProposedStatus));

        // No bytes were uploaded — so no document_assets and no document_candidates yet.
        Assert.Empty(await fx.Db.DocumentAssets.ToListAsync());
        Assert.Empty(await fx.Db.DocumentCandidates.ToListAsync());

        // Each manifest item has a Discovered job queued so we can resume later.
        var jobs = await fx.Db.IngestionJobs.Where(j => j.BatchId == scan.BatchId).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(IngestionStage.Discovered, j.Stage));
    }

    [Fact]
    public async Task ScoreManifestAsync_AssignsBandsAndRecommendedActions()
    {
        using var fx = new OrchestratorFixture();

        var items = new List<ManifestItem>
        {
            new() { RelativePath = "Payers/BCBS/Amendment.pdf", Name = "Amendment.pdf",
                    SizeBytes = 50_000, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "application/pdf" },
            new() { RelativePath = "Marketing/poster.jpg", Name = "poster.jpg",
                    SizeBytes = 1_000_000, LastModifiedUtc = fx.Clock.UtcNow, MimeType = "image/jpeg" }
        };

        var scan = (await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), items, CancellationToken.None)).Value!;

        var amendment = scan.Items.Single(i => i.Name == "Amendment.pdf");
        Assert.True(amendment.Confidence >= 0.60m, $"expected likely+, got {amendment.Confidence}");
        Assert.Contains(amendment.Band, new[] { ManifestBands.Strong, ManifestBands.Likely });
        Assert.Equal(ManifestRecommendedActions.Select, amendment.RecommendedAction);

        var poster = scan.Items.Single(i => i.Name == "poster.jpg");
        // Image files have no contract signals — the classifier should never
        // recommend selecting them, even if it lands them in the Possible band.
        Assert.NotEqual(ManifestRecommendedActions.Select, poster.RecommendedAction);
        Assert.NotEqual(ManifestBands.Strong, poster.Band);
        Assert.NotEqual(ManifestBands.Likely, poster.Band);
    }

    [Fact]
    public async Task ScoreManifestAsync_EmitsManifestCreatedAuditEvent()
    {
        using var fx = new OrchestratorFixture();

        var scan = (await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), new List<ManifestItem>
        {
            new() { RelativePath = "x.pdf", Name = "x.pdf", SizeBytes = 100, LastModifiedUtc = fx.Clock.UtcNow }
        }, CancellationToken.None)).Value!;

        var audit = await fx.Db.AuditEvents
            .Where(a => a.ResourceId == scan.BatchId)
            .ToListAsync();

        Assert.Contains(audit, a => a.EventType == "ingestion.manifest.created");
    }

    [Fact]
    public async Task ScoreManifestAsync_RescoringSamePath_UpdatesExistingSourceObject()
    {
        using var fx = new OrchestratorFixture();

        var item = new ManifestItem
        {
            RelativePath = "Payers/BCBS/Amendment.pdf",
            Name = "Amendment.pdf",
            SizeBytes = 12_000,
            LastModifiedUtc = fx.Clock.UtcNow,
            MimeType = "application/pdf"
        };

        await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), [item], CancellationToken.None);
        await fx.Orchestrator.ScoreManifestAsync(NewRequest(fx), [item], CancellationToken.None);

        // Two manifest scans of the same file should not double-create the source_object.
        var sourceObjects = await fx.Db.SourceObjects.Where(s => s.ConnectionId == fx.ConnectionId).ToListAsync();
        Assert.Single(sourceObjects);
    }

    private static IngestionRequest NewRequest(OrchestratorFixture fx) => new()
    {
        TenantId = fx.TenantId,
        InitiatedByUserId = fx.UserId,
        ConnectionId = fx.ConnectionId,
        SourceType = SourceTypes.LocalFolder
    };
}
