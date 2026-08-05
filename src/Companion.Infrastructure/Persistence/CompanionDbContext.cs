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

        b.Entity<Conversation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.HasIndex(x => x.UserId);
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
            e.Property(x => x.MemoryKind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.MemoryId);
        });

        b.Entity<MemoryRevision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MemoryKind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Actor).HasMaxLength(100);
            e.HasIndex(x => x.MemoryId);
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
