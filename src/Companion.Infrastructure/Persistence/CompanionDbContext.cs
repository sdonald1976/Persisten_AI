using Companion.Core.Domain;
using Companion.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Companion.Infrastructure.Persistence;

/// <summary>
/// The authoritative relational store (SQLite). Everything the companion knows lives here;
/// embeddings are kept as BLOBs on the memory rows and can be regenerated at any time.
/// </summary>
public sealed class CompanionDbContext : DbContext
{
    public CompanionDbContext(DbContextOptions<CompanionDbContext> options) : base(options) { }

    public DbSet<UserProfile> Users => Set<UserProfile>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<SemanticMemory> SemanticMemories => Set<SemanticMemory>();
    public DbSet<EpisodicMemory> EpisodicMemories => Set<EpisodicMemory>();
    public DbSet<MemoryEvidence> Evidence => Set<MemoryEvidence>();
    public DbSet<MemoryRevision> Revisions => Set<MemoryRevision>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectAlias> ProjectAliases => Set<ProjectAlias>();
    public DbSet<ProjectEvent> ProjectEvents => Set<ProjectEvent>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<OpenLoop> OpenLoops => Set<OpenLoop>();
    public DbSet<FeedbackRecord> Feedback => Set<FeedbackRecord>();
    public DbSet<PendingClarification> PendingClarifications => Set<PendingClarification>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY / compare DateTimeOffset directly. This built-in converter
        // stores it as a sortable long that preserves the offset, so temporal ordering works.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserProfile>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(200);
        });

        b.Entity<FeedbackRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Rating).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Conversation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<PendingClarification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.AmbiguityType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Resolving the next message queries (ConversationId, Status); reads are user-scoped.
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.HasIndex(x => new { x.UserId, x.Status });
        });

        b.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ConversationId, x.Timestamp });
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SemanticMemory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Validity).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Origin).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.Status });
            ConfigureEmbedding(e.Property(x => x.Embedding));
            // Evidence is queried by MemoryId (polymorphic across kinds), not navigated.
            e.Ignore(x => x.Evidence);
        });

        b.Entity<EpisodicMemory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.EpisodeStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.TimePrecision).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.Status });
            e.Ignore(x => x.IsOpenLoop);
            ConfigureEmbedding(e.Property(x => x.Embedding));
            e.Ignore(x => x.Evidence);
        });

        b.Entity<MemoryEvidence>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.MemoryKind).HasConversion<string>().HasMaxLength(20);
            // Ownership-scoped provenance lookups query (UserId, MemoryId).
            e.HasIndex(x => new { x.UserId, x.MemoryId });
        });

        b.Entity<MemoryRevision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.MemoryKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Actor).HasMaxLength(100);
            // Ownership-scoped audit-trail lookups query (UserId, MemoryId).
            e.HasIndex(x => new { x.UserId, x.MemoryId });
        });

        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.UserId);
            ConfigureEmbedding(e.Property(x => x.Embedding));
        });

        b.Entity<ProjectAlias>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Alias).HasMaxLength(300);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ProjectId);
        });

        b.Entity<ProjectEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(x => new { x.ProjectId, x.Timestamp });
        });

        b.Entity<Decision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.ProjectId);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<OpenLoop>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Owner).HasMaxLength(100);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => x.ProjectId);
            ConfigureEmbedding(e.Property(x => x.Embedding));
        });
    }

    private static void ConfigureEmbedding(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<float[]?> property)
    {
        // The converter is non-null (float[] <-> byte[]); EF applies it only to non-null
        // values and stores null as SQL NULL, so the nullability mismatch is safe.
#pragma warning disable CS8620
        property.HasConversion(EmbeddingConversion.Converter)
                .Metadata.SetValueComparer(EmbeddingConversion.Comparer);
#pragma warning restore CS8620
        property.HasColumnType("BLOB");
    }
}
