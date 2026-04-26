using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using PracticeX.Application.Common;
using PracticeX.Application.SourceDiscovery.Storage;
using PracticeX.Discovery.Classification;
using PracticeX.Discovery.Signatures;
using PracticeX.Discovery.Validation;
using PracticeX.Domain.Sources;
using PracticeX.Infrastructure.Persistence;
using PracticeX.Infrastructure.SourceDiscovery.Complexity;
using PracticeX.Infrastructure.SourceDiscovery.Ingestion;
using PracticeX.Infrastructure.SourceDiscovery.Pricing;

namespace PracticeX.Tests.SourceDiscovery.Support;

/// <summary>
/// Wires the IngestionOrchestrator against EF Core in-memory + a fake document
/// storage so manifest/bundle paths can be exercised without a real Postgres.
/// The unique-index dedupe (per-tenant SHA) is NOT enforced by the in-memory
/// provider; tests that care about that path must run against real Postgres.
/// </summary>
internal sealed class OrchestratorFixture : IDisposable
{
    public PracticeXDbContext Db { get; }
    public IngestionOrchestrator Orchestrator { get; }
    public FakeDocumentStorage Storage { get; } = new();
    public TestClock Clock { get; } = new();
    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid UserId { get; } = Guid.NewGuid();
    public Guid ConnectionId { get; } = Guid.NewGuid();

    public OrchestratorFixture()
    {
        var options = new DbContextOptionsBuilder<PracticeXDbContext>()
            .UseInMemoryDatabase(databaseName: $"orch-{Guid.NewGuid():N}")
            .Options;
        Db = new TestPracticeXDbContext(options);
        Db.Database.EnsureCreated();

        // Seed a connection so source_objects FK is satisfied.
        Db.SourceConnections.Add(new SourceConnection
        {
            Id = ConnectionId,
            TenantId = TenantId,
            SourceType = SourceTypes.LocalFolder,
            Status = SourceConnectionStatus.Connected,
            CreatedByUserId = UserId,
            CreatedAt = Clock.UtcNow
        });
        Db.SaveChanges();

        var profiler = new CompositeComplexityProfiler(
            new PdfComplexityProfiler(),
            new ExcelComplexityProfiler(),
            new DocxComplexityProfiler(),
            new PlainTextComplexityProfiler());

        var signatureDetector = new CompositeSignatureDetector(new ISignatureDetector[]
        {
            new PdfSignatureDetector(),
            new DocxSignatureDetector()
        });

        Orchestrator = new IngestionOrchestrator(
            Db,
            Storage,
            new RuleBasedContractClassifier(),
            new BasicDocumentValidityInspector(),
            profiler,
            new PlaceholderPricingPolicy(),
            signatureDetector,
            Clock,
            NullLogger<IngestionOrchestrator>.Instance);
    }

    public void Dispose() => Db.Dispose();
}

internal sealed class TestClock : IClock
{
    private DateTimeOffset _now = new(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// Test-only DbContext that registers JsonDocument-to-string value converters
/// so the EF InMemory provider can store the production jsonb columns. The
/// Postgres provider in production does this natively.
/// </summary>
internal sealed class TestPracticeXDbContext(DbContextOptions<PracticeXDbContext> options) : PracticeXDbContext(options)
{
    protected override void OnModelCreatingExtra(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<JsonDocument, string>(
            v => v == null ? "null" : v.RootElement.GetRawText(),
            s => JsonDocument.Parse(string.IsNullOrEmpty(s) ? "null" : s, default));

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(JsonDocument))
                {
                    property.SetValueConverter(converter);
                }
            }
        }
    }
}

internal sealed class FakeDocumentStorage : IDocumentStorage
{
    private readonly Dictionary<string, byte[]> _bytes = new();

    public Task<StoredDocument> StoreAsync(Guid tenantId, string suggestedName, Stream content, string mimeType, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var bytes = ms.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var uri = $"fake://{tenantId}/{sha}";
        _bytes[uri] = bytes;
        return Task.FromResult(new StoredDocument(uri, sha, bytes.LongLength));
    }

    public Task<Stream> OpenReadAsync(string storageUri, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new MemoryStream(_bytes[storageUri]));
}
