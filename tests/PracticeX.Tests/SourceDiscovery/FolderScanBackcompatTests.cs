using System.Text;
using Microsoft.EntityFrameworkCore;
using PracticeX.Application.SourceDiscovery.Connectors;
using PracticeX.Application.SourceDiscovery.Ingestion;
using PracticeX.Domain.Documents;
using PracticeX.Domain.Sources;
using PracticeX.Tests.SourceDiscovery.Support;

namespace PracticeX.Tests.SourceDiscovery;

/// <summary>
/// Verifies the legacy /folder/scan path (via IngestAsync) still works after
/// the orchestrator was extended for the manifest+bundle flow, AND now also
/// populates the validity columns introduced by the new path.
/// </summary>
public class FolderScanBackcompatTests
{
    [Fact]
    public async Task IngestAsync_LegacyPath_StillCreatesAssetAndCandidate_WithValidityPopulated()
    {
        using var fx = new OrchestratorFixture();

        var bytes = Encoding.UTF8.GetBytes("Service agreement body. Annual renewal date 2027-01-01.");
        var discovery = new DiscoveryResult
        {
            Items = new List<DiscoveredItem>
            {
                new()
                {
                    ExternalId = "local:Vendors/Olympus Renewal.txt",
                    Name = "Olympus Renewal.txt",
                    MimeType = "text/plain",
                    RelativePath = "Vendors/Olympus Renewal.txt",
                    SizeBytes = bytes.LongLength,
                    InlineContent = bytes
                }
            },
            Notes = []
        };

        var result = await fx.Orchestrator.IngestAsync(NewRequest(fx), discovery, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var batch = await fx.Db.IngestionBatches.SingleAsync();
        Assert.Equal(IngestionBatchPhase.Complete, batch.Phase); // legacy path stays at 'complete'

        var asset = await fx.Db.DocumentAssets.SingleAsync();
        Assert.NotNull(asset.ExtractionRoute);
        Assert.NotNull(asset.ValidityStatus);
        Assert.Equal(ExtractionRoutes.LocalText, asset.ExtractionRoute);
        Assert.Equal(ValidityStatuses.Valid, asset.ValidityStatus);

        var candidate = await fx.Db.DocumentCandidates.SingleAsync();
        Assert.Equal(asset.Id, candidate.DocumentAssetId);
    }

    [Fact]
    public async Task IngestAsync_EmitsBatchCompletedAuditEvent()
    {
        using var fx = new OrchestratorFixture();

        var discovery = new DiscoveryResult
        {
            Items = new List<DiscoveredItem>
            {
                new()
                {
                    ExternalId = "local:x.txt", Name = "x.txt", MimeType = "text/plain",
                    RelativePath = "x.txt", SizeBytes = 4, InlineContent = "body"u8.ToArray()
                }
            },
            Notes = []
        };
        await fx.Orchestrator.IngestAsync(NewRequest(fx), discovery, CancellationToken.None);

        var audit = await fx.Db.AuditEvents.Select(a => a.EventType).ToListAsync();
        Assert.Contains("ingestion.batch.completed", audit);
    }

    private static IngestionRequest NewRequest(OrchestratorFixture fx) => new()
    {
        TenantId = fx.TenantId,
        InitiatedByUserId = fx.UserId,
        ConnectionId = fx.ConnectionId,
        SourceType = SourceTypes.LocalFolder
    };
}
