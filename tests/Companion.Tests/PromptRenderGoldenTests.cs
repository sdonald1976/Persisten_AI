using System.Text;
using Companion.Core.Domain;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Byte golden for the production prompt — the exact string the chat model receives.
///
/// This is the highest-value characterization in the refactor safety net, because
/// <c>ContextPacketRenderer</c> is 532 lines that turn roughly twenty optional sections into
/// one string, and the extraction phases move the code that FILLS those sections. A
/// reordered section, a dropped separator or a changed heading would alter every reply Ava
/// gives while every existing test stayed green.
///
/// The packets are constructed rather than captured from a live turn: a real turn's packet
/// depends on retrieval, the clock and the model, so a golden built from one would fail for
/// reasons that have nothing to do with the renderer.
/// </summary>
public class PromptRenderGoldenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string GoldenPath => Path.Combine(
        RepoRoot(), "tests", "Companion.Tests", "Goldens", "prompt-render.txt");

    private static Message Msg(MessageRole role, string content, int minute)
        => new()
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{minute:D12}"),
            UserId = "usr-scott",
            ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Role = role,
            Content = content,
            Timestamp = Now.AddMinutes(minute),
        };

    /// <summary>The minimum packet: nothing optional set at all.</summary>
    private static ContextPacket Bare() => new()
    {
        UserMessage = "What did we decide about the shed?",
        MaxPromptTokens = 4000,
    };

    /// <summary>Every optional section populated, so none can be dropped unnoticed.</summary>
    private static ContextPacket Full() => new()
    {
        UserMessage = "What did we decide about the shed?",
        Persona = "You are Ava.",
        RecentMessages =
        [
            Msg(MessageRole.User, "The squirrel defeated the baffle again.", 1),
            Msg(MessageRole.Assistant, "Third time this month.", 2),
        ],
        Memories =
        [
            new ContextItem
            {
                Text = "Scott has a dog named Ruby.",
                Provenance = ContextProvenance.DirectStatement,
            },
            new ContextItem
            {
                Text = "The shed roof needs replacing before winter.",
                Provenance = ContextProvenance.Inferred,
                Note = "unresolved",
            },
        ],
        RelationshipNote = "You have spoken most days for two months.",
        Musing = "You were wondering whether the baffle was ever the problem.",
        CuriosityQuestion = "Did the shed quote ever come through?",
        MoodNote = "You are in good spirits.",
        RegisterNote = "verbosity=terse",
        InterpretationNote = "He is asking about a decision, not restarting it.",
        LearnedKnowledge = ["A baffle is a squirrel guard on a bird feeder."],
        FamiliarityNote = "You know each other well.",
        TemporalNote = "It is Thursday afternoon; you last spoke yesterday.",
        PreferenceNotes = ["He asked you not to hedge."],
        AttentionNotes = ["The shed decision is unresolved."],
        ProcedureNotes = ["When he asks what was decided, answer with the decision first."],
        CapabilityNote = "You can read files and search the web.",
        SharedPerspectiveNotes = ["You both think the squirrel has earned it."],
        UncertaintyNotes = ["You are not sure the quote arrived."],
        Diagnostics = ["retrieval: 2 of 9 selected"],
        ToolResults = "web.search -> 3 results",
        MaxPromptTokens = 4000,
        EstimatedTokens = 512,
    };

    /// <summary>Trimming is a behaviour of its own and must be pinned separately.</summary>
    private static ContextPacket Trimmed() => Full() with
    {
        TrimmedSections = ["memories", "recent"],
        EstimatedTokens = 3990,
        MaxPromptTokens = 4000,
    };

    private static IEnumerable<(string Name, ContextPacket Packet)> Cases()
    {
        yield return ("bare", Bare());
        yield return ("full", Full());
        yield return ("trimmed", Trimmed());
        yield return ("clarification", Bare() with
        {
            ClarificationQuestion = "Do you mean the garden shed or the bike shed?",
        });
    }

    private static string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# Rendered production prompt goldens. Regenerate only as a reviewed change.\n");
        foreach (var (name, packet) in Cases())
        {
            sb.Append("\n===== ").Append(name).Append(" =====\n");
            sb.Append(packet.Render().ReplaceLineEndings("\n"));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void TheRenderedPrompt_IsExactlyAsPinned()
    {
        Assert.True(File.Exists(GoldenPath),
            $"golden missing at {GoldenPath}. Generate it deliberately and commit it as a "
            + "reviewed change.");

        var expected = File.ReadAllText(GoldenPath).ReplaceLineEndings("\n");
        Assert.Equal(expected, Render());
    }

    [Fact]
    public void EveryOptionalSection_IsExercisedByTheFullCase()
    {
        // The golden only protects sections it actually renders. This fails when a new
        // optional section is added to ContextPacket and not added to Full() — which is the
        // moment the golden would silently stop covering it.
        var full = Full();
        var unset = typeof(ContextPacket).GetProperties()
            .Where(p => p.Name is not (nameof(ContextPacket.UserMessage)
                or nameof(ContextPacket.MaxPromptTokens)
                or nameof(ContextPacket.EstimatedTokens)
                or nameof(ContextPacket.TrimmedSections)      // pinned by the trimmed case
                or nameof(ContextPacket.ClarificationQuestion) // pinned by its own case
                or nameof(ContextPacket.Identities)            // structured, not a text section
                or nameof(ContextPacket.Project)
                or nameof(ContextPacket.OpenLoops)))
            .Where(p =>
            {
                var value = p.GetValue(full);
                return value is null
                       || (value is System.Collections.ICollection { Count: 0 });
            })
            .Select(p => p.Name)
            .ToList();

        Assert.True(unset.Count == 0,
            "these ContextPacket sections are not exercised by the golden, so a change to "
            + "how they render would go unnoticed: " + string.Join(", ", unset));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
