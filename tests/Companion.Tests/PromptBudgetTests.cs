using Companion.Core.Domain;
using Companion.Core.Services;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Backlog #7: a regression guard on prompt size. A small local model degrades quietly as its
/// context fills — replies get vaguer, instructions get ignored — long before anything errors.
/// This builds a deliberately maximal packet (every section populated at realistic worst case)
/// and asserts the rendered prompt stays inside a budget a 8B model can actually use well.
/// If a new section or a fatter default pushes it over, this fails at the moment it's introduced.
/// </summary>
public class PromptBudgetTests
{
    /// <summary>
    /// Ceiling for the pathological case — EVERY optional section present at once, at its cap,
    /// including a pasted wall of text. Measured at ~5.6k when this guard was written (down from
    /// ~7.2k before tool results, pasted turns, and the recent-conversation section were bounded).
    /// The headroom is deliberate: this catches REGRESSIONS, it is not a target to optimize
    /// toward — a real turn costs ~1k (see the typical test), and trimming genuine context to
    /// chase a smaller number would trade correctness for tokens.
    /// </summary>
    private const int MaxWorstCaseTokens = 6000;

    /// <summary>Ceiling for a NORMAL turn — the shape almost every real turn takes.</summary>
    private const int MaxTypicalTokens = 1500;

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Sentence(int words, string word) => string.Join(' ', Enumerable.Repeat(word, words));

    /// <summary>Every section filled at or above its realistic cap.</summary>
    private static ContextPacket MaximalPacket() => new()
    {
        UserMessage = Sentence(60, "question"),
        Persona = Sentence(150, "persona"),
        Identities = new PromptIdentityContext
        {
            CompanionName = "Ava", CompanionGender = "female", CompanionPronouns = "she/her",
            UserName = "Scott",
            RelationshipLines = Enumerable.Range(0, 4).Select(i => Sentence(15, $"relationship{i}")).ToList(),
        },
        RecentMessages = Enumerable.Range(0, 10).Select(i => new Message
        {
            Id = Guid.NewGuid(), UserId = "u", ConversationId = Guid.NewGuid(),
            Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
            // One of them is a pasted wall of text — the realistic worst case for anyone who
            // drops a spec or a log into the chat.
            Content = i == 3 ? Sentence(1200, "pasted") : Sentence(60, $"recent{i}"),
            Timestamp = Now.AddMinutes(i),
        }).ToList(),
        Memories = Enumerable.Range(0, 10).Select(i => new ContextItem
        {
            Text = Sentence(30, $"memory{i}"),
            Provenance = i % 3 == 0 ? ContextProvenance.Outdated : ContextProvenance.DirectStatement,
            Owner = i % 4 == 0 ? MemoryOwner.Shared : MemoryOwner.User,
        }).ToList(),
        OpenLoops = Enumerable.Range(0, 5).Select(i => new RetrievedOpenLoop
        {
            OpenLoop = new OpenLoop
            {
                Id = Guid.NewGuid(), UserId = "u", Description = Sentence(25, $"loop{i}"),
                Owner = "user", Status = OpenLoopStatus.Open, CreatedAt = Now,
            },
            Score = 1, Reason = "test",
        }).ToList(),
        RelationshipNote = Sentence(40, "relationship"),
        Musing = Sentence(50, "musing"),
        CuriosityQuestion = Sentence(30, "curiosity"),
        MoodNote = Sentence(25, "mood"),
        RegisterNote = Sentence(20, "register"),
        FamiliarityNote = Sentence(25, "familiarity"),
        TemporalNote = Sentence(25, "temporal"),
        PreferenceNotes = Enumerable.Range(0, 5).Select(i => Sentence(20, $"preference{i}")).ToList(),
        AttentionNotes = Enumerable.Range(0, 5).Select(i => Sentence(20, $"attention{i}")).ToList(),
        ProcedureNotes = Enumerable.Range(0, 3).Select(i => Sentence(30, $"procedure{i}")).ToList(),
        CapabilityNote = Sentence(60, "capability"),
        SharedPerspectiveNotes = Enumerable.Range(0, 3).Select(i => Sentence(25, $"perspective{i}")).ToList(),
        // Tool results are clipped by the loop (MaxSectionChars) — use that worst case.
        ToolResults = new string('t', 2500),
        UncertaintyNotes = Enumerable.Range(0, 3).Select(i => Sentence(20, $"uncertainty{i}")).ToList(),
    };

    [Fact]
    public void AMaximalPacket_StaysWithinTheWorstCaseBudget()
    {
        var rendered = MaximalPacket().Render();
        var tokens = ContextAssembler.EstimateTokens(rendered);

        Assert.True(tokens <= MaxWorstCaseTokens,
            $"The worst-case context packet renders to ~{tokens} tokens, over the {MaxWorstCaseTokens} " +
            "budget. Something new is in the prompt, or a section's cap grew — trim it, budget it, " +
            "or raise the ceiling deliberately.");
    }

    [Fact]
    public void ATypicalPacket_IsFarCheaperThanTheWorstCase()
    {
        // What an ordinary turn actually looks like: some memories, a short recent exchange,
        // companion state, no tools, no project, no procedures.
        var typical = new ContextPacket
        {
            UserMessage = Sentence(20, "question"),
            Persona = Sentence(80, "persona"),
            RecentMessages = Enumerable.Range(0, 6).Select(i => new Message
            {
                Id = Guid.NewGuid(), UserId = "u", ConversationId = Guid.NewGuid(),
                Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = Sentence(30, $"recent{i}"), Timestamp = Now.AddMinutes(i),
            }).ToList(),
            Memories = Enumerable.Range(0, 4).Select(i => new ContextItem
            {
                Text = Sentence(20, $"memory{i}"), Provenance = ContextProvenance.DirectStatement,
            }).ToList(),
            MoodNote = Sentence(15, "mood"),
            TemporalNote = Sentence(12, "temporal"),
            FamiliarityNote = Sentence(12, "familiarity"),
        };

        var tokens = ContextAssembler.EstimateTokens(typical.Render());
        Assert.True(tokens <= MaxTypicalTokens,
            $"A typical turn costs ~{tokens} tokens, over the {MaxTypicalTokens} budget.");
    }

    [Fact]
    public void OnePastedWallOfText_CannotOwnTheWholePrompt()
    {
        // Dropping a long document into the chat must not crowd out memory, persona and state
        // for the rest of the conversation.
        var huge = new string('x', 20_000);
        var packet = new ContextPacket
        {
            UserMessage = "what do you think?",
            RecentMessages = new[]
            {
                new Message
                {
                    Id = Guid.NewGuid(), UserId = "u", ConversationId = Guid.NewGuid(),
                    Role = MessageRole.User, Content = huge, Timestamp = Now,
                },
            },
        };

        var rendered = packet.Render();
        Assert.True(ContextAssembler.EstimateTokens(rendered) < 600);
        Assert.Contains("[…]", rendered); // clipped, and visibly so
    }

    [Fact]
    public void AnEmptyPacket_CostsAlmostNothing()
    {
        // Sections with no content must be omitted entirely, not rendered as empty headings —
        // a fresh user should not pay for machinery they have no data for.
        var minimal = new ContextPacket { UserMessage = "hi" };
        var tokens = ContextAssembler.EstimateTokens(minimal.Render());

        Assert.True(tokens < 400, $"An empty packet costs ~{tokens} tokens; the framing has grown.");
    }

    [Fact]
    public void TheEstimate_DescribesTheWholeRenderedPacket()
    {
        // Regression guard for the audit finding: the estimate used to count only memories,
        // recent messages and the user message, under-reporting everything else in the prompt.
        var packet = MaximalPacket();
        var rendered = packet.Render();

        Assert.Contains("perspective0", rendered);
        Assert.Contains("capability", rendered);
        Assert.True(ContextAssembler.EstimateTokens(rendered)
            > ContextAssembler.EstimateTokens(packet.UserMessage) * 5);
    }
}
