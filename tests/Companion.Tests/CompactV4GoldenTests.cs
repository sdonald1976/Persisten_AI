using System.Text;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Byte goldens for <c>CompactV4</c>.
///
/// The audit reconciliation found plan/2 well covered (804 corpus plans through the producer
/// hop) and plan/4 covered only by <c>StartsWith</c>, <c>Contains</c>, and one RELATIVE
/// equality — <c>v3.Replace("[plan/3]", "[plan/4]") == v4</c>. Nothing pinned the actual
/// bytes, so the entire FRAME rendering could change without a single test failing.
///
/// The corpus cannot supply this: it is frozen plan/2 and contains no frames at all. So the
/// cases are constructed, one per structural axis the FRAME section has — transition kind,
/// narrator kind, narration policy, boundaries, character roster, and the frameless case
/// that must still cost zero tokens.
///
/// The golden is full text rather than a hash, because there are few enough cases to read
/// and the point is that a reviewer can SEE what the mouth would receive.
/// </summary>
public class CompactV4GoldenTests
{
    private const string User = "usr-scott";
    private const string Ava = "companion-ava";

    public static string GoldenPath => Path.Combine(
        RepoRoot(), "tests", "Companion.Tests", "Goldens", "compact-v4.txt");

    private static PlanV3.PlanV3 Plan(Frame? frame, IReadOnlyList<PlanItem>? items = null)
        => new()
        {
            Protocol = PlanV4Codec.Protocol,
            TraceId = Guid.Parse("77777777-1111-2222-3333-444444444444"),
            Participants =
            [
                new Participant(User, ParticipantRole.user, "Scott"),
                new Participant(Ava, ParticipantRole.companion, "Ava"),
            ],
            Act = "respond",
            Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
            Items = items ?? [],
            Register = PlanV3Codec.Canonicalize(new RegisterVector()),
            Frame = frame,
        };

    private static Frame Fiction(
        FrameTransition transition = FrameTransition.@continue,
        string? active = "keeper",
        FrameNarrator? narrator = null,
        FrameNarration narration = FrameNarration.licensed,
        IReadOnlyList<FrameBoundaryRef>? boundaries = null)
        => new()
        {
            Mode = FrameMode.fiction,
            Transition = transition,
            SceneRef = "scene-7c1f",
            Narration = narration,
            Continuity = FrameContinuity.maintain,
            ActiveCompanionCharacterId = active,
            Narrator = narrator,
            Characters =
            [
                new FrameCharacter("keeper", "the lighthouse keeper", Ava),
                // A second companion-controlled character, so the switch case has somewhere
                // legal to go: the validator rejects switching Ava into the user's part.
                new FrameCharacter("innkeeper", "the innkeeper", Ava),
                new FrameCharacter("sailor", "the sailor", User),
            ],
            Boundaries = boundaries ?? [],
        };

    /// <summary>One case per structural axis of the FRAME section.</summary>
    private static IEnumerable<(string Name, PlanV3.PlanV3 Plan)> Cases()
    {
        // The zero-cost case: plan/4 with no frame must render exactly like plan/3 did.
        yield return ("no-frame", Plan(null));

        yield return ("enter", Plan(Fiction(FrameTransition.enter)));
        yield return ("continue", Plan(Fiction(FrameTransition.@continue)));
        yield return ("switch-scene", Plan(Fiction(FrameTransition.switchScene, active: "innkeeper")));
        yield return ("exit", Plan(Fiction(FrameTransition.exit)));

        yield return ("narrator-character-first-person", Plan(Fiction(
            narrator: new FrameNarrator(
                NarratorKind.character, "keeper", "keeper", NarrativePerson.first))));
        yield return ("narrator-external-third-person", Plan(Fiction(
            active: null,
            narrator: new FrameNarrator(
                NarratorKind.external, null, "sailor", NarrativePerson.third))));

        yield return ("narration-forbidden", Plan(Fiction(narration: FrameNarration.forbidden)));

        yield return ("with-boundary", Plan(Fiction(
            boundaries:
            [
                // A fixed boundary id, so the golden is stable across runs.
                new FrameBoundaryRef(
                    "fb-1", "no third-person narration",
                    "11111111-2222-3333-4444-555555555555"),
            ])));

        // A frame carrying ordinary plan items, so the interaction between the two sections
        // is pinned rather than only the frame in isolation.
        yield return ("frame-with-items", Plan(
            Fiction(FrameTransition.@continue),
            [
                new PlanItem
                {
                    Id = "i1",
                    Type = "fact",
                    Category = RenderCategory.memory,
                    Policy = ExpressionPolicy.may_express,
                    Text = "Scott has a dog named Ruby.",
                    Source = "memory",
                },
            ]));
    }

    public static string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# CompactV4 goldens. Regenerate only as a reviewed change.\n");
        foreach (var (name, plan) in Cases())
        {
            sb.Append("\n===== ").Append(name).Append(" =====\n");
            sb.Append(PlanV4Codec.CompactV4(plan).ReplaceLineEndings("\n"));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void CompactV4_RendersExactlyAsPinned()
    {
        Assert.True(File.Exists(GoldenPath),
            $"golden missing at {GoldenPath}. Generate it deliberately and commit it as a "
            + "reviewed change — a golden that regenerates itself proves nothing.");

        var expected = File.ReadAllText(GoldenPath).ReplaceLineEndings("\n");
        Assert.Equal(expected, Render());
    }

    [Fact]
    public void EveryFrameAxis_IsRepresented()
    {
        // A golden is only as good as its case list; this fails if an axis is dropped.
        var names = Cases().Select(c => c.Name).ToList();

        Assert.Contains("no-frame", names);
        foreach (var transition in Enum.GetValues<FrameTransition>())
        {
            var expected = PlanV4Codec.Kebab(transition);
            Assert.True(
                names.Any(n => n.Replace("-", "").Contains(
                    expected.Replace("-", ""), StringComparison.OrdinalIgnoreCase)),
                $"no golden case covers the '{expected}' transition");
        }
        foreach (var kind in Enum.GetValues<NarratorKind>())
            Assert.Contains(names, n => n.Contains(kind.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }
}
