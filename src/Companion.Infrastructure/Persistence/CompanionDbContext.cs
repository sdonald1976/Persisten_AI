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
    public DbSet<EmotionalSignal> EmotionalSignals => Set<EmotionalSignal>();
    public DbSet<Reflection> Reflections => Set<Reflection>();
    public DbSet<Curiosity> Curiosities => Set<Curiosity>();

    /// <summary>Her own experiences — what happened to her, never facts about the user.</summary>
    public DbSet<Experience> Experiences => Set<Experience>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();
    public DbSet<Anticipation> Anticipations => Set<Anticipation>();
    public DbSet<CompanionPreference> CompanionPreferences => Set<CompanionPreference>();
    public DbSet<AttentionItem> AttentionItems => Set<AttentionItem>();
    public DbSet<MemoryAssociation> MemoryAssociations => Set<MemoryAssociation>();
    public DbSet<SharedExperiencePerspective> SharedExperiencePerspectives => Set<SharedExperiencePerspective>();
    public DbSet<Procedure> Procedures => Set<Procedure>();
    public DbSet<ProcedureStep> ProcedureSteps => Set<ProcedureStep>();
    public DbSet<ProcedureRevision> ProcedureRevisions => Set<ProcedureRevision>();
    public DbSet<CapabilityDescriptor> Capabilities => Set<CapabilityDescriptor>();
    public DbSet<ModelCallRecord> ModelCalls => Set<ModelCallRecord>();
    public DbSet<ToolCallRecord> ToolCalls => Set<ToolCallRecord>();
    public DbSet<ShadowComparison> ShadowComparisons => Set<ShadowComparison>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY / compare DateTimeOffset directly. This built-in converter
        // stores it as a sortable long that preserves the offset, so temporal ordering works.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ModelCallRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(40);
            e.Property(x => x.Operation).HasMaxLength(20);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(200);
            // Stats aggregate over a time window; pruning deletes below a cutoff.
            e.HasIndex(x => x.Timestamp);
        });

        b.Entity<ToolCallRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Tool).HasMaxLength(80);
            e.Property(x => x.Arguments).HasMaxLength(2000);
            e.Property(x => x.Code).HasMaxLength(40);
            // Read newest-first per user.
            e.HasIndex(x => new { x.UserId, x.Timestamp });
        });

        b.Entity<ShadowComparison>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Subject).HasMaxLength(80);
            e.Property(x => x.Legacy).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Applied).HasMaxLength(20);
            // Bounded like ToolCallRecord.Arguments: this column only ever holds text when capture
            // is explicitly switched on, and a cap keeps an accident cheap.
            e.Property(x => x.Input).HasMaxLength(2000);
            // Agreement is read per subject over a window; disagreements newest-first.
            e.HasIndex(x => new { x.Subject, x.Timestamp });
        });

        b.Entity<UserProfile>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.PersonalityPreset).HasMaxLength(40);
            e.Property(x => x.CompanionName).HasMaxLength(80);
            e.Property(x => x.CompanionGender).HasMaxLength(40);
            e.Property(x => x.CompanionPronouns).HasMaxLength(40);
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

        b.Entity<EmotionalSignal>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Sentiment).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Label).HasMaxLength(60);
            e.Property(x => x.Evidence).HasMaxLength(200);
            e.Property(x => x.Topic).HasMaxLength(120);
            // The tracker reads a user's most recent signals in time order.
            e.HasIndex(x => new { x.UserId, x.Timestamp });
        });

        b.Entity<Experience>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(50);
            e.Property(x => x.Kind).HasMaxLength(50);
            e.Property(x => x.Text).HasMaxLength(500);
            // Read as a window since the last reflection watermark, and pruned by age.
            e.HasIndex(x => new { x.UserId, x.At });
        });

        b.Entity<Reflection>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Musing).HasMaxLength(2000);
            // The diary is read newest-first per user (latest watermark, recent musings).
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            // Trains of thought are read by thread.
            e.HasIndex(x => new { x.UserId, x.ThreadId });
            ConfigureEmbedding(e.Property(x => x.Embedding));
            e.Ignore(x => x.HasMusing);
        });

        b.Entity<AttentionItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(160);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.SourceId).HasMaxLength(80);
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.UserId, x.Status, x.LastActivatedAt });
        });

        b.Entity<MemoryAssociation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.AssociationType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Evidence).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.SourceMemoryId });
            e.HasIndex(x => new { x.UserId, x.TargetMemoryId });
        });

        b.Entity<SharedExperiencePerspective>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.Property(x => x.Evidence).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.ExperienceId });
        });

        b.Entity<Procedure>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Access).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Evidence).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Revisions).WithOne().HasForeignKey(x => x.ProcedureId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProcedureStep>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Instruction).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.ProcedureId, x.Order });
        });

        b.Entity<ProcedureRevision>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserId, x.ProcedureId, x.Timestamp });
        });

        b.Entity<CapabilityDescriptor>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.InputTypes).HasMaxLength(200);
            e.Property(x => x.OutputTypes).HasMaxLength(200);
            e.Property(x => x.Provider).HasMaxLength(120);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Availability).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Curiosity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Question).HasMaxLength(300);
            e.Property(x => x.About).HasMaxLength(120);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Voicing queries (UserId, Status); provenance points back at the reflection.
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => x.ReflectionId);
        });

        b.Entity<CompanionPreference>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.Reason).HasMaxLength(400);
            e.HasIndex(x => x.UserId);
            ConfigureEmbedding(e.Property(x => x.Embedding));
        });

        b.Entity<Anticipation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Evidence).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // Surfacing reads a user's open anticipations by event day.
            e.HasIndex(x => new { x.UserId, x.Status });
            e.Ignore(x => x.IsOpen);
        });

        b.Entity<OutboundMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Text).HasMaxLength(500);
            e.Property(x => x.Source).HasMaxLength(100);
            // The budget check reads a user's most recent send.
            e.HasIndex(x => new { x.UserId, x.SentAt });
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
            e.Property(x => x.FinishReason).HasMaxLength(40);
            e.Property(x => x.ModelUsed).HasMaxLength(200);
            e.HasIndex(x => new { x.ConversationId, x.Timestamp });
            e.HasIndex(x => x.UserId);
            // A message cannot exist without its conversation — enforced by a real FK, not just app
            // code. Deleting a conversation cascades to its messages.
            e.HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SemanticMemory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Validity).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Origin).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(20);
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
            e.Property(x => x.Owner).HasConversion<string>().HasMaxLength(20);
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
