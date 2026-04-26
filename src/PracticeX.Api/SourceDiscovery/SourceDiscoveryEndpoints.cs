using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PracticeX.Application.Common;
using PracticeX.Application.SourceDiscovery.Connectors;
using PracticeX.Application.SourceDiscovery.Ingestion;
using PracticeX.Application.SourceDiscovery.Outlook;
using PracticeX.Domain.Documents;
using PracticeX.Domain.Sources;
using PracticeX.Infrastructure.Persistence;

namespace PracticeX.Api.SourceDiscovery;

public static class SourceDiscoveryEndpoints
{
    /// <summary>
    /// In-memory OAuth state map. Production replaces with a distributed cache or
    /// signed/encrypted state cookie. Keyed by the random state token; carries the
    /// connection id we should attach the resulting credentials to.
    /// </summary>
    private static readonly ConcurrentDictionary<string, OAuthStateEntry> OAuthStates = new();

    public static IEndpointRouteBuilder MapSourceDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources").WithTags("source-discovery");

        group.MapGet("/connectors", GetConnectors);
        group.MapGet("/connections", ListConnections);
        group.MapPost("/connections", CreateConnection);
        group.MapDelete("/connections/{connectionId:guid}", DeleteConnection);

        group.MapPost("/connections/{connectionId:guid}/folder/scan", ScanFolderUpload).DisableAntiforgery();
        group.MapPost("/connections/{connectionId:guid}/folder/manifest", ScoreFolderManifest);
        group.MapPost("/connections/{connectionId:guid}/folder/bundles", IngestFolderBundle).DisableAntiforgery();

        group.MapGet("/connections/{connectionId:guid}/outlook/oauth/start", StartOutlookOAuth);
        group.MapGet("/outlook/oauth/callback", HandleOutlookOAuthCallback);
        group.MapPost("/connections/{connectionId:guid}/outlook/scan", ScanOutlook);

        group.MapGet("/batches", ListBatches);
        group.MapGet("/batches/{batchId:guid}", GetBatch);
        group.MapDelete("/batches/{batchId:guid}", DeleteBatch);
        group.MapDelete("/batches", DeleteAllBatches);
        group.MapGet("/candidates", ListCandidates);
        group.MapPost("/candidates/{candidateId:guid}/queue-review", QueueCandidateReview);
        group.MapPost("/candidates/{candidateId:guid}/retry", RetryCandidate);

        return app;
    }

    private static Ok<IReadOnlyCollection<ConnectorDescriptorDto>> GetConnectors(IConnectorRegistry registry)
    {
        var descriptors = registry.Describe()
            .Select(d => new ConnectorDescriptorDto(
                SourceType: d.SourceType,
                DisplayName: d.DisplayName,
                Summary: d.Summary,
                AuthMode: d.AuthMode.ToString().ToLowerInvariant(),
                IsReadOnly: d.IsReadOnly,
                Status: d.Status,
                SupportedMimeTypes: d.SupportedMimeTypes))
            .ToList();
        return TypedResults.Ok<IReadOnlyCollection<ConnectorDescriptorDto>>(descriptors);
    }

    private static async Task<Ok<IReadOnlyCollection<SourceConnectionDto>>> ListConnections(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var connections = await db.SourceConnections
            .Where(c => c.TenantId == userContext.TenantId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new SourceConnectionDto(
                c.Id,
                c.SourceType,
                c.Status,
                c.DisplayName,
                c.OauthSubject,
                c.LastSyncAt,
                c.CreatedAt,
                c.LastError))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyCollection<SourceConnectionDto>>(connections);
    }

    private static async Task<Created<SourceConnectionDto>> CreateConnection(
        [FromBody] CreateConnectionRequest request,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        IClock clock,
        IConnectorRegistry registry,
        CancellationToken cancellationToken)
    {
        var connector = registry.Resolve(request.SourceType)
            ?? throw new BadHttpRequestException($"Unknown source_type '{request.SourceType}'.");

        var status = connector.Describe().AuthMode switch
        {
            ConnectorAuthMode.None => SourceConnectionStatus.Connected,
            ConnectorAuthMode.OAuth => SourceConnectionStatus.AwaitingAuth,
            _ => SourceConnectionStatus.Draft
        };

        var connection = new SourceConnection
        {
            TenantId = userContext.TenantId,
            SourceType = request.SourceType,
            DisplayName = request.DisplayName,
            Status = status,
            CreatedByUserId = userContext.UserId,
            CreatedAt = clock.UtcNow
        };
        db.SourceConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/sources/connections/{connection.Id}", new SourceConnectionDto(
            connection.Id,
            connection.SourceType,
            connection.Status,
            connection.DisplayName,
            connection.OauthSubject,
            connection.LastSyncAt,
            connection.CreatedAt,
            connection.LastError));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteConnection(
        Guid connectionId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }

        connection.Status = SourceConnectionStatus.Disabled;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IngestionBatchSummaryDto>, BadRequest<ProblemDetails>, NotFound>> ScanFolderUpload(
        Guid connectionId,
        HttpRequest httpRequest,
        PracticeXDbContext db,
        IConnectorRegistry registry,
        IIngestionOrchestrator orchestrator,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }
        if (connection.SourceType != SourceTypes.LocalFolder)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Wrong connector",
                Detail = $"Connection {connectionId} is not a local_folder connection."
            });
        }

        if (!httpRequest.HasFormContentType)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Multipart required",
                Detail = "Upload requires a multipart/form-data body with file parts."
            });
        }

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var files = form.Files;
        if (files.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "No files",
                Detail = "At least one file part is required."
            });
        }

        var inputs = new List<DiscoveryInput>(files.Count);
        var openedStreams = new List<Stream>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Length == 0)
                {
                    continue;
                }
                var stream = file.OpenReadStream();
                openedStreams.Add(stream);

                // Browsers post relative paths via webkitRelativePath, exposed as the
                // "FileName" or as a sibling form field "paths[i]". Support both.
                var relativePath = form.TryGetValue($"paths[{i}]", out var pathValue) && pathValue.Count > 0
                    ? pathValue[0]
                    : file.FileName;

                inputs.Add(new DiscoveryInput
                {
                    Name = Path.GetFileName(file.FileName),
                    RelativePath = relativePath,
                    MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Content = stream,
                    SizeBytes = file.Length
                });
            }

            var connector = registry.Resolve(SourceTypes.LocalFolder)
                ?? throw new InvalidOperationException("Local folder connector not registered.");

            var discovery = await connector.DiscoverAsync(new DiscoveryRequest
            {
                TenantId = userContext.TenantId,
                ConnectionId = connection.Id,
                InitiatedByUserId = userContext.UserId,
                Inputs = inputs
            }, cancellationToken);

            if (!discovery.IsSuccess)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = discovery.Error?.Code ?? "discovery_failed",
                    Detail = discovery.Error?.Message
                });
            }

            var ingest = await orchestrator.IngestAsync(new IngestionRequest
            {
                TenantId = userContext.TenantId,
                InitiatedByUserId = userContext.UserId,
                ConnectionId = connection.Id,
                SourceType = SourceTypes.LocalFolder,
                Notes = form.TryGetValue("notes", out var notes) ? notes.ToString() : null
            }, discovery.Value!, cancellationToken);

            if (!ingest.IsSuccess)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = ingest.Error?.Code ?? "ingest_failed",
                    Detail = ingest.Error?.Message
                });
            }

            connection.LastSyncAt = DateTimeOffset.UtcNow;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var summary = ingest.Value!;
            return TypedResults.Ok(new IngestionBatchSummaryDto(
                BatchId: summary.BatchId,
                FileCount: summary.FileCount,
                CandidateCount: summary.CandidateCount,
                SkippedCount: summary.SkippedCount,
                ErrorCount: summary.ErrorCount,
                Status: summary.Status,
                Items: summary.Items.Select(MapItem).ToList()));
        }
        finally
        {
            foreach (var s in openedStreams)
            {
                await s.DisposeAsync();
            }
        }
    }

    private static async Task<Results<Ok<ManifestScanResponse>, BadRequest<ProblemDetails>, NotFound>> ScoreFolderManifest(
        Guid connectionId,
        [FromBody] ManifestScanRequest request,
        PracticeXDbContext db,
        IIngestionOrchestrator orchestrator,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }
        if (connection.SourceType != SourceTypes.LocalFolder)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Wrong connector",
                Detail = $"Connection {connectionId} is not a local_folder connection."
            });
        }
        if (request.Items is null || request.Items.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Empty manifest",
                Detail = "At least one manifest item is required."
            });
        }

        var items = request.Items
            .Select(i => new ManifestItem
            {
                RelativePath = i.RelativePath,
                Name = i.Name,
                SizeBytes = i.SizeBytes,
                LastModifiedUtc = i.LastModifiedUtc,
                MimeType = i.MimeType
            })
            .ToList();

        var result = await orchestrator.ScoreManifestAsync(new IngestionRequest
        {
            TenantId = userContext.TenantId,
            InitiatedByUserId = userContext.UserId,
            ConnectionId = connection.Id,
            SourceType = SourceTypes.LocalFolder,
            Notes = request.Notes
        }, items, cancellationToken);

        if (!result.IsSuccess)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = result.Error?.Code ?? "manifest_failed",
                Detail = result.Error?.Message
            });
        }

        var scored = result.Value!;
        var dtoItems = scored.Items.Select(i => new ManifestScoredItemDto(
            ManifestItemId: i.ManifestItemId,
            RelativePath: i.RelativePath,
            Name: i.Name,
            SizeBytes: i.SizeBytes,
            CandidateType: i.CandidateType,
            Confidence: i.Confidence,
            ReasonCodes: i.ReasonCodes,
            RecommendedAction: i.RecommendedAction,
            Band: i.Band,
            CounterpartyHint: i.CounterpartyHint
        )).ToList();

        return TypedResults.Ok(new ManifestScanResponse(
            BatchId: scored.BatchId,
            Phase: scored.Phase,
            TotalItems: dtoItems.Count,
            StrongCount: dtoItems.Count(i => i.Band == ManifestBands.Strong),
            LikelyCount: dtoItems.Count(i => i.Band == ManifestBands.Likely),
            PossibleCount: dtoItems.Count(i => i.Band == ManifestBands.Possible),
            SkippedCount: dtoItems.Count(i => i.Band == ManifestBands.Skipped),
            Items: dtoItems));
    }

    private static async Task<Results<Ok<IngestionBatchSummaryDto>, BadRequest<ProblemDetails>, NotFound>> IngestFolderBundle(
        Guid connectionId,
        [FromQuery] Guid batchId,
        HttpRequest httpRequest,
        PracticeXDbContext db,
        IConnectorRegistry registry,
        IIngestionOrchestrator orchestrator,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }
        if (connection.SourceType != SourceTypes.LocalFolder)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Wrong connector",
                Detail = $"Connection {connectionId} is not a local_folder connection."
            });
        }

        if (!httpRequest.HasFormContentType)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Multipart required",
                Detail = "Bundle upload requires multipart/form-data."
            });
        }

        var form = await httpRequest.ReadFormAsync(cancellationToken);
        var files = form.Files;
        if (files.Count == 0)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "No files",
                Detail = "Bundle must include at least one selected file."
            });
        }

        var inputs = new List<DiscoveryInput>(files.Count);
        var openedStreams = new List<Stream>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Length == 0)
                {
                    continue;
                }
                var stream = file.OpenReadStream();
                openedStreams.Add(stream);

                var relativePath = form.TryGetValue($"paths[{i}]", out var pathValue) && pathValue.Count > 0
                    ? pathValue[0]
                    : file.FileName;
                var manifestItemId = form.TryGetValue($"manifestItemIds[{i}]", out var midValue) && midValue.Count > 0
                    ? midValue[0]
                    : null;

                inputs.Add(new DiscoveryInput
                {
                    Name = Path.GetFileName(file.FileName),
                    RelativePath = relativePath,
                    MimeType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Content = stream,
                    SizeBytes = file.Length,
                    ExternalIdHint = manifestItemId
                });
            }

            var connector = registry.Resolve(SourceTypes.LocalFolder)
                ?? throw new InvalidOperationException("Local folder connector not registered.");

            var discovery = await connector.DiscoverAsync(new DiscoveryRequest
            {
                TenantId = userContext.TenantId,
                ConnectionId = connection.Id,
                InitiatedByUserId = userContext.UserId,
                Inputs = inputs
            }, cancellationToken);

            if (!discovery.IsSuccess)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = discovery.Error?.Code ?? "discovery_failed",
                    Detail = discovery.Error?.Message
                });
            }

            var ingest = await orchestrator.IngestBundleAsync(new IngestionRequest
            {
                TenantId = userContext.TenantId,
                InitiatedByUserId = userContext.UserId,
                ConnectionId = connection.Id,
                SourceType = SourceTypes.LocalFolder,
                Notes = form.TryGetValue("notes", out var notes) ? notes.ToString() : null
            }, batchId, discovery.Value!, cancellationToken);

            if (!ingest.IsSuccess)
            {
                return TypedResults.BadRequest(new ProblemDetails
                {
                    Title = ingest.Error?.Code ?? "ingest_failed",
                    Detail = ingest.Error?.Message
                });
            }

            connection.LastSyncAt = DateTimeOffset.UtcNow;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var summary = ingest.Value!;
            return TypedResults.Ok(new IngestionBatchSummaryDto(
                BatchId: summary.BatchId,
                FileCount: summary.FileCount,
                CandidateCount: summary.CandidateCount,
                SkippedCount: summary.SkippedCount,
                ErrorCount: summary.ErrorCount,
                Status: summary.Status,
                Items: summary.Items.Select(MapItem).ToList()));
        }
        finally
        {
            foreach (var s in openedStreams)
            {
                await s.DisposeAsync();
            }
        }
    }

    private static async Task<Results<Ok<OutlookOAuthStartResponse>, BadRequest<ProblemDetails>, NotFound>> StartOutlookOAuth(
        Guid connectionId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        IMicrosoftGraphOAuthService oauthService,
        IOptions<MicrosoftGraphOptions> graphOptions,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }
        if (connection.SourceType != SourceTypes.OutlookMailbox)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Wrong connector" });
        }
        if (!oauthService.IsConfigured)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Microsoft Graph not configured",
                Detail = "Set MicrosoftGraph:ClientId, MicrosoftGraph:ClientSecret, and MicrosoftGraph:TenantId. See docs/source-discovery.md."
            });
        }

        var state = Guid.NewGuid().ToString("N");
        var redirectUri = ResolveRedirectUri(graphOptions.Value);
        OAuthStates[state] = new OAuthStateEntry(connection.Id, userContext.TenantId, DateTimeOffset.UtcNow.AddMinutes(15));

        var url = oauthService.BuildAuthorizationUrl(state, redirectUri);
        return TypedResults.Ok(new OutlookOAuthStartResponse(url, state));
    }

    private static async Task<Results<Ok<SourceConnectionDto>, BadRequest<ProblemDetails>>> HandleOutlookOAuthCallback(
        [FromQuery] string code,
        [FromQuery] string state,
        PracticeXDbContext db,
        IClock clock,
        IMicrosoftGraphOAuthService oauthService,
        IMicrosoftGraphTokenStore tokenStore,
        IOptions<MicrosoftGraphOptions> graphOptions,
        CancellationToken cancellationToken)
    {
        if (!OAuthStates.TryRemove(state, out var entry) || entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Invalid or expired OAuth state" });
        }

        var connection = await db.SourceConnections.FirstOrDefaultAsync(c => c.Id == entry.ConnectionId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Connection not found" });
        }

        var redirectUri = ResolveRedirectUri(graphOptions.Value);
        try
        {
            var token = await oauthService.AcquireTokenAsync(code, redirectUri, cancellationToken);
            await tokenStore.SaveAsync(connection.Id, new StoredGraphToken(
                Subject: token.Subject,
                RefreshToken: token.RefreshToken,
                CachedAccessToken: token.AccessToken,
                CachedAccessTokenExpiresAt: token.AccessTokenExpiresAt,
                Scope: token.Scope), cancellationToken);

            connection.OauthSubject = token.Subject;
            connection.ScopeSet = token.Scope;
            connection.Status = SourceConnectionStatus.Connected;
            connection.LastError = null;
            connection.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            connection.Status = SourceConnectionStatus.Error;
            connection.LastError = ex.Message;
            connection.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Token acquisition failed",
                Detail = ex.Message
            });
        }

        return TypedResults.Ok(new SourceConnectionDto(
            connection.Id,
            connection.SourceType,
            connection.Status,
            connection.DisplayName,
            connection.OauthSubject,
            connection.LastSyncAt,
            connection.CreatedAt,
            connection.LastError));
    }

    private static async Task<Results<Ok<IngestionBatchSummaryDto>, BadRequest<ProblemDetails>, NotFound>> ScanOutlook(
        Guid connectionId,
        [FromBody] OutlookScanRequest request,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        IConnectorRegistry registry,
        IIngestionOrchestrator orchestrator,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var connection = await db.SourceConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.TenantId == userContext.TenantId, cancellationToken);
        if (connection is null)
        {
            return TypedResults.NotFound();
        }
        if (connection.SourceType != SourceTypes.OutlookMailbox)
        {
            return TypedResults.BadRequest(new ProblemDetails { Title = "Wrong connector" });
        }
        if (connection.Status != SourceConnectionStatus.Connected)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = "Connection not authorized",
                Detail = "Complete OAuth authorization first."
            });
        }

        var connector = registry.Resolve(SourceTypes.OutlookMailbox)
            ?? throw new InvalidOperationException("Outlook connector not registered.");

        var discovery = await connector.DiscoverAsync(new DiscoveryRequest
        {
            TenantId = userContext.TenantId,
            ConnectionId = connection.Id,
            InitiatedByUserId = userContext.UserId,
            MaxItems = request.Top ?? 25,
            Since = request.Since
        }, cancellationToken);

        if (!discovery.IsSuccess)
        {
            connection.LastError = discovery.Error?.Message;
            connection.Status = SourceConnectionStatus.Error;
            connection.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = discovery.Error?.Code ?? "discovery_failed",
                Detail = discovery.Error?.Message
            });
        }

        var ingest = await orchestrator.IngestAsync(new IngestionRequest
        {
            TenantId = userContext.TenantId,
            InitiatedByUserId = userContext.UserId,
            ConnectionId = connection.Id,
            SourceType = SourceTypes.OutlookMailbox
        }, discovery.Value!, cancellationToken);

        if (!ingest.IsSuccess)
        {
            return TypedResults.BadRequest(new ProblemDetails
            {
                Title = ingest.Error?.Code ?? "ingest_failed",
                Detail = ingest.Error?.Message
            });
        }

        connection.LastSyncAt = clock.UtcNow;
        connection.LastError = null;
        connection.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var summary = ingest.Value!;
        return TypedResults.Ok(new IngestionBatchSummaryDto(
            BatchId: summary.BatchId,
            FileCount: summary.FileCount,
            CandidateCount: summary.CandidateCount,
            SkippedCount: summary.SkippedCount,
            ErrorCount: summary.ErrorCount,
            Status: summary.Status,
            Items: summary.Items.Select(MapItem).ToList()));
    }

    private static async Task<Ok<IReadOnlyCollection<IngestionBatchDto>>> ListBatches(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 20, 1, 100);
        var batches = await db.IngestionBatches
            .Where(b => b.TenantId == userContext.TenantId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(take)
            .Select(b => new IngestionBatchDto(
                b.Id,
                b.SourceType,
                b.SourceConnectionId,
                b.Status,
                b.FileCount,
                b.CandidateCount,
                b.SkippedCount,
                b.ErrorCount,
                b.CreatedAt,
                b.CompletedAt,
                b.Notes))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyCollection<IngestionBatchDto>>(batches);
    }

    private static async Task<Results<Ok<IngestionBatchDto>, NotFound>> GetBatch(
        Guid batchId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var b = await db.IngestionBatches.FirstOrDefaultAsync(x => x.Id == batchId && x.TenantId == userContext.TenantId, cancellationToken);
        if (b is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(new IngestionBatchDto(
            b.Id, b.SourceType, b.SourceConnectionId, b.Status,
            b.FileCount, b.CandidateCount, b.SkippedCount, b.ErrorCount,
            b.CreatedAt, b.CompletedAt, b.Notes));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteBatch(
        Guid batchId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var batch = await db.IngestionBatches.FirstOrDefaultAsync(
            x => x.Id == batchId && x.TenantId == userContext.TenantId, cancellationToken);
        if (batch is null)
        {
            return TypedResults.NotFound();
        }

        await CascadeDeleteBatchAsync(db, batchId, userContext.TenantId, userContext.UserId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<DeleteAllBatchesResult>> DeleteAllBatches(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        CancellationToken cancellationToken)
    {
        var batchIds = await db.IngestionBatches
            .Where(x => x.TenantId == userContext.TenantId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in batchIds)
        {
            await CascadeDeleteBatchAsync(db, id, userContext.TenantId, userContext.UserId, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new DeleteAllBatchesResult(batchIds.Count));
    }

    private static async Task CascadeDeleteBatchAsync(
        PracticeXDbContext db,
        Guid batchId,
        Guid tenantId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        // Drop in dependency order. document_assets and source_objects can be
        // shared across batches via per-tenant SHA dedupe, so we leave them in
        // place — only batch-scoped rows are removed. Audit events are
        // immutable and stay.
        var jobs = await db.IngestionJobs
            .Where(j => j.BatchId == batchId && j.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var sourceObjectIds = jobs.Select(j => j.SourceObjectId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var assetIds = jobs.Select(j => j.DocumentAssetId).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

        // Delete document_candidates whose source_object was created by jobs in this batch.
        var candidates = await db.DocumentCandidates
            .Where(c => c.TenantId == tenantId
                && (sourceObjectIds.Contains(c.SourceObjectId!.Value) || assetIds.Contains(c.DocumentAssetId)))
            .ToListAsync(cancellationToken);
        if (candidates.Count > 0)
        {
            // Drop their review tasks first
            var candidateIds = candidates.Select(c => c.Id).ToList();
            var reviewTasks = await db.ReviewTasks
                .Where(r => r.TenantId == tenantId && r.ResourceType == "document_candidate" && candidateIds.Contains(r.ResourceId))
                .ToListAsync(cancellationToken);
            if (reviewTasks.Count > 0) db.ReviewTasks.RemoveRange(reviewTasks);
            db.DocumentCandidates.RemoveRange(candidates);
        }

        db.IngestionJobs.RemoveRange(jobs);

        var batch = await db.IngestionBatches.FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is not null)
        {
            db.AuditEvents.Add(new PracticeX.Domain.Audit.AuditEvent
            {
                TenantId = tenantId,
                ActorType = "user",
                ActorId = actorUserId,
                EventType = "ingestion.batch.deleted",
                ResourceType = "ingestion_batch",
                ResourceId = batchId,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    sourceType = batch.SourceType,
                    fileCount = batch.FileCount,
                    candidateCount = batch.CandidateCount,
                    phase = batch.Phase,
                    deletedJobs = jobs.Count,
                    deletedCandidates = candidates.Count
                }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.IngestionBatches.Remove(batch);
        }
    }

    private static async Task<Ok<IReadOnlyCollection<DocumentCandidateDto>>> ListCandidates(
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        [FromQuery] string? status,
        [FromQuery] int? limit,
        [FromQuery] Guid? batchId,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.DocumentCandidates
            .AsNoTracking()
            .Where(c => c.TenantId == userContext.TenantId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(c => c.Status == status);
        }

        if (batchId.HasValue)
        {
            // Join via ingestion job for batch filtering.
            var assetIds = db.IngestionJobs
                .Where(j => j.BatchId == batchId.Value && j.DocumentAssetId != null)
                .Select(j => j.DocumentAssetId!.Value);
            query = query.Where(c => assetIds.Contains(c.DocumentAssetId));
        }

        var candidates = await query
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var dtos = candidates.Select(c => new DocumentCandidateDto(
            c.Id,
            c.SourceObjectId,
            c.DocumentAssetId,
            c.CandidateType,
            c.Confidence,
            c.Status,
            DeserializeReasonCodes(c.ReasonCodesJson),
            c.ClassifierVersion,
            c.OriginFilename,
            c.RelativePath,
            c.CounterpartyHint,
            c.CreatedAt)).ToList();

        return TypedResults.Ok<IReadOnlyCollection<DocumentCandidateDto>>(dtos);
    }

    private static async Task<Results<Ok, NotFound>> QueueCandidateReview(
        Guid candidateId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var candidate = await db.DocumentCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId && c.TenantId == userContext.TenantId, cancellationToken);
        if (candidate is null)
        {
            return TypedResults.NotFound();
        }

        candidate.Status = DocumentCandidateStatus.PendingReview;
        candidate.UpdatedAt = clock.UtcNow;

        var hasTask = await db.ReviewTasks.AnyAsync(
            t => t.ResourceType == "document_candidate" && t.ResourceId == candidate.Id,
            cancellationToken);
        if (!hasTask)
        {
            db.ReviewTasks.Add(new Domain.Workflow.ReviewTask
            {
                TenantId = userContext.TenantId,
                ResourceType = "document_candidate",
                ResourceId = candidate.Id,
                Reason = "manual_queue",
                Priority = 2,
                Decision = "pending",
                CreatedAt = clock.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, NotFound>> RetryCandidate(
        Guid candidateId,
        PracticeXDbContext db,
        ICurrentUserContext userContext,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var candidate = await db.DocumentCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId && c.TenantId == userContext.TenantId, cancellationToken);
        if (candidate is null)
        {
            return TypedResults.NotFound();
        }
        candidate.Status = DocumentCandidateStatus.Candidate;
        candidate.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok();
    }

    private static IReadOnlyList<string> DeserializeReasonCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return [];
        }
    }

    private static IngestionItemDto MapItem(IngestionItemSummary i) => new(
        i.SourceObjectId,
        i.DocumentAssetId,
        i.DocumentCandidateId,
        i.Name,
        i.CandidateType,
        i.Confidence,
        i.ReasonCodes,
        i.Status,
        i.RelativePath);

    private static string ResolveRedirectUri(MicrosoftGraphOptions options)
    {
        // Microsoft requires the redirect URI sent on token exchange to match the
        // one sent on /authorize exactly, so both call sites resolve it the same way.
        return Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_REDIRECT_URI")
            ?? options.RedirectUri;
    }

    private sealed record OAuthStateEntry(Guid ConnectionId, Guid TenantId, DateTimeOffset ExpiresAt);
}
