using Microsoft.EntityFrameworkCore;
using PracticeX.Domain.Audit;
using PracticeX.Domain.Contracts;
using PracticeX.Domain.Documents;
using PracticeX.Domain.Evidence;
using PracticeX.Domain.Organization;
using PracticeX.Domain.Sources;
using PracticeX.Domain.Workflow;

namespace PracticeX.Infrastructure.Persistence;

public class PracticeXDbContext(DbContextOptions<PracticeXDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Facility> Facilities => Set<Facility>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<SourceObject> SourceObjects => Set<SourceObject>();
    public DbSet<IngestionBatch> IngestionBatches => Set<IngestionBatch>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<DocumentAsset> DocumentAssets => Set<DocumentAsset>();
    public DbSet<DocumentCandidate> DocumentCandidates => Set<DocumentCandidate>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<ContractRecord> Contracts => Set<ContractRecord>();
    public DbSet<ContractField> ContractFields => Set<ContractField>();
    public DbSet<EvidenceLink> EvidenceLinks => Set<EvidenceLink>();
    public DbSet<ReviewTask> ReviewTasks => Set<ReviewTask>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureOrganization(modelBuilder);
        ConfigureSources(modelBuilder);
        ConfigureDocuments(modelBuilder);
        ConfigureContracts(modelBuilder);
        ConfigureEvidence(modelBuilder);
        ConfigureWorkflow(modelBuilder);
        ConfigureAudit(modelBuilder);
        OnModelCreatingExtra(modelBuilder);
    }

    /// <summary>
    /// Hook for test contexts to register provider-specific value converters
    /// (e.g. JsonDocument → string for the EF InMemory provider). No-op in
    /// production.
    /// </summary>
    protected virtual void OnModelCreatingExtra(ModelBuilder modelBuilder) { }

    private static void ConfigureOrganization(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants", "org");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.DataRegion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.BaaStatus).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<Facility>(entity =>
        {
            entity.ToTable("facilities", "org");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Name).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.ToTable("users", "org");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles", "org");
            entity.HasKey(x => x.Id);
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Permissions).HasColumnType("jsonb");
        });

        modelBuilder.Entity<RoleAssignment>(entity =>
        {
            entity.ToTable("role_assignments", "org");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.UserId, x.FacilityId, x.RoleId }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Facility>().WithMany().HasForeignKey(x => x.FacilityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });
    }

    private static void ConfigureSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceConnection>(entity =>
        {
            entity.ToTable("source_connections", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.SourceType, x.DisplayName });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.SourceType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(240);
            entity.Property(x => x.ConfigJson).HasColumnType("jsonb");
            entity.Property(x => x.CredentialsJson).HasColumnType("jsonb");
            entity.Property(x => x.LastError).HasColumnType("text");
        });

        modelBuilder.Entity<SourceObject>(entity =>
        {
            entity.ToTable("source_objects", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ConnectionId, x.ExternalId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Sha256 });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ExternalId).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Uri).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(512).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.ObjectKind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(1024);
            entity.Property(x => x.ParentExternalId).HasMaxLength(512);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
            entity.Property(x => x.ProposedStatus).HasMaxLength(40);
            entity.Property(x => x.QuickFingerprint).HasMaxLength(96);
        });
    }

    private static void ConfigureDocuments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IngestionBatch>(entity =>
        {
            entity.ToTable("ingestion_batches", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceConnection>().WithMany().HasForeignKey(x => x.SourceConnectionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.SourceType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Notes).HasColumnType("text");
            entity.Property(x => x.Phase).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<IngestionJob>(entity =>
        {
            entity.ToTable("ingestion_jobs", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IngestionBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceObject>().WithMany().HasForeignKey(x => x.SourceObjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentAsset>().WithMany().HasForeignKey(x => x.DocumentAssetId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Stage).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ErrorCode).HasMaxLength(120);
        });

        modelBuilder.Entity<DocumentAsset>(entity =>
        {
            entity.ToTable("document_assets", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Sha256 }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceObject>().WithMany().HasForeignKey(x => x.SourceObjectId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.StorageUri).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MimeType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.TextStatus).HasMaxLength(40).IsRequired();
            entity.Property(x => x.OcrStatus).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ExtractionRoute).HasMaxLength(40);
            entity.Property(x => x.ValidityStatus).HasMaxLength(40);
            entity.Property(x => x.ComplexityTier).HasMaxLength(2);
            entity.Property(x => x.ComplexityFactorsJson).HasColumnType("jsonb");
            entity.Property(x => x.ComplexityBlockersJson).HasColumnType("jsonb");
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
            entity.Property(x => x.EstimatedComplexityHours).HasPrecision(8, 2);
            entity.HasIndex(x => new { x.TenantId, x.ComplexityTier });
            entity.Property(x => x.LayoutJson).HasColumnType("jsonb");
            entity.Property(x => x.LayoutProvider).HasMaxLength(40);
            entity.Property(x => x.LayoutModel).HasMaxLength(80);
            entity.Property(x => x.ExtractedFieldsJson).HasColumnType("jsonb");
            entity.Property(x => x.ExtractedSubtype).HasMaxLength(60);
            entity.Property(x => x.ExtractedSchemaVersion).HasMaxLength(40);
            entity.Property(x => x.ExtractorName).HasMaxLength(80);
            entity.Property(x => x.ExtractionStatus).HasMaxLength(40);
            entity.Property(x => x.ExtractedFullText).HasColumnType("text");
            entity.Property(x => x.LlmExtractedFieldsJson).HasColumnType("jsonb");
            entity.Property(x => x.LlmExtractorModel).HasMaxLength(120);
            entity.Property(x => x.LlmExtractionStatus).HasMaxLength(40);
        });

        modelBuilder.Entity<DocumentCandidate>(entity =>
        {
            entity.ToTable("document_candidates", "doc");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Status });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentAsset>().WithMany().HasForeignKey(x => x.DocumentAssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Facility>().WithMany().HasForeignKey(x => x.FacilityHintId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceObject>().WithMany().HasForeignKey(x => x.SourceObjectId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.CandidateType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ReasonCodesJson).HasColumnType("jsonb");
            entity.Property(x => x.ClassifierVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.OriginFilename).HasMaxLength(512);
            entity.Property(x => x.RelativePath).HasMaxLength(1024);
        });
    }

    private static void ConfigureContracts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Counterparty>(entity =>
        {
            entity.ToTable("counterparties", "contract");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Aliases).HasColumnType("jsonb");
            entity.Property(x => x.PayerIdentifier).HasMaxLength(160);
        });

        modelBuilder.Entity<ContractRecord>(entity =>
        {
            entity.ToTable("contracts", "contract");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.FacilityId, x.Status });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Facility>().WithMany().HasForeignKey(x => x.FacilityId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Counterparty>().WithMany().HasForeignKey(x => x.CounterpartyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ContractType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
        });

        modelBuilder.Entity<ContractField>(entity =>
        {
            entity.ToTable("contract_fields", "contract");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ContractId, x.FieldKey, x.SchemaVersion }).IsUnique();
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ContractRecord>().WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.SchemaVersion).HasMaxLength(80).IsRequired();
            entity.Property(x => x.FieldKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ValueJson).HasColumnType("jsonb");
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.ReviewStatus).HasMaxLength(40).IsRequired();
        });
    }

    private static void ConfigureEvidence(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvidenceLink>(entity =>
        {
            entity.ToTable("evidence_links", "evidence");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DocumentAsset>().WithMany().HasForeignKey(x => x.DocumentAssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceObject>().WithMany().HasForeignKey(x => x.SourceObjectId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ResourceType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PageRefs).HasColumnType("jsonb");
            entity.Property(x => x.Quote).HasColumnType("text").IsRequired();
        });
    }

    private static void ConfigureWorkflow(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReviewTask>(entity =>
        {
            entity.ToTable("review_tasks", "workflow");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.Decision, x.Priority });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ResourceType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Decision).HasMaxLength(40).IsRequired();
        });
    }

    private static void ConfigureAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events", "audit");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ActorType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ResourceType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PriorValueHash).HasMaxLength(128);
            entity.Property(x => x.NewValueHash).HasMaxLength(128);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
        });
    }
}
