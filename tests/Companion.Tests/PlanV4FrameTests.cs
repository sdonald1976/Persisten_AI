using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// plan/4 contract and serializer. The frame changes how the plan is READ and never what is
/// true; these tests hold that line structurally, and hold the zero-cost property that lets
/// one protocol carry both real and fiction turns.
/// </summary>
public class PlanV4FrameTests
{
    private const string User = "usr-scott";
    private const string Companion = "companion-ava";

    private static PlanV3.PlanV3 Plan(Frame? frame = null, IReadOnlyList<PlanItem>? items = null)
        => new()
        {
            Protocol = PlanV4Codec.Protocol,
            TraceId = Guid.Parse("77777777-1111-2222-3333-444444444444"),
            Participants =
            [
                new Participant(User, ParticipantRole.user, "Scott"),
                new Participant(Companion, ParticipantRole.companion, "Ava"),
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
        IReadOnlyList<FrameCharacter>? characters = null,
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
            Characters = characters ??
            [
                new FrameCharacter("keeper", "the lighthouse keeper", Companion),
                new FrameCharacter("sailor", "the sailor", User),
            ],
            Boundaries = boundaries ?? [],
        };

    // ---- the zero-cost property ----------------------------------------------------------

    [Fact]
    public void AnOrdinaryTurn_SerializesNoFrameSection_AndCostsNothing()
    {
        var v4 = PlanV4Codec.CompactV4(Plan());

        Assert.StartsWith("[plan/4]\r\n", v4);
        Assert.DoesNotContain("FRAME", v4);

        // Byte-identical to plan/3 apart from the protocol tag: one protocol, both turn kinds.
        var v3 = PlanV3Codec.CompactV3(Plan() with { Protocol = "plan/3" });
        Assert.Equal(v3.Replace("[plan/3]", "[plan/4]"), v4);
    }

    [Fact]
    public void CompactV3_IsUntouchedByTheFrame()
    {
        // plan/3 stays frozen: a frame present or absent makes no difference to it, which is
        // what keeps the 804-plan corpus goldens meaningful.
        var withFrame = Plan(Fiction()) with { Protocol = "plan/3" };
        var without = Plan() with { Protocol = "plan/3" };

        Assert.Equal(PlanV3Codec.CompactV3(without), PlanV3Codec.CompactV3(withFrame));
    }

    // ---- serialization -------------------------------------------------------------------

    [Fact]
    public void AFictionTurn_SerializesFrameAfterControlAndBeforeTheSections()
    {
        var plan = Plan(
            Fiction(narrator: new FrameNarrator(NarratorKind.character, "keeper", "keeper", NarrativePerson.first)),
            items: [new PlanItem
            {
                Id = "i1", Type = "scene", Category = RenderCategory.state,
                Policy = ExpressionPolicy.must_express, Source = "procedure",
                Text = "The storm has not let up since nightfall.",
            }]);

        var v4 = PlanV4Codec.CompactV4(plan);

        Assert.Contains("FRAME (you are in a story", v4);
        Assert.Contains("mode = fiction  transition = continue  scene = scene-7c1f", v4);
        Assert.Contains("narrator = the lighthouse keeper (first person, following the lighthouse keeper)", v4);
        Assert.Contains("narration = licensed  continuity = maintain", v4);
        Assert.Contains("you-play = the lighthouse keeper", v4);
        Assert.Contains("they-play = the sailor", v4);

        // Ordering: CONTROL, then FRAME, then the policy sections.
        Assert.True(v4.IndexOf("CONTROL", StringComparison.Ordinal)
                    < v4.IndexOf("FRAME (", StringComparison.Ordinal));
        Assert.True(v4.IndexOf("FRAME (", StringComparison.Ordinal)
                    < v4.IndexOf("SAY (", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExitTurn_Serializes_AndCarriesNothingToContinue()
    {
        var exit = new Frame
        {
            Mode = FrameMode.real,
            Transition = FrameTransition.exit,
            SceneRef = "scene-7c1f",
            Characters = [new FrameCharacter("keeper", "the lighthouse keeper", Companion)],
        };

        var v4 = PlanV4Codec.CompactV4(Plan(exit));

        // The turn that must stop the story reaches the mouth — the failure rev 1 had.
        Assert.Contains("FRAME (the story is over", v4);
        Assert.Contains("transition = exit  targetMode = real", v4);
        Assert.Contains("narration = forbidden", v4);
        // Nothing left to obey, and nothing that reads as an invitation to continue.
        Assert.DoesNotContain("scene = ", v4);
        Assert.DoesNotContain("you-play", v4);
        Assert.DoesNotContain("they-play", v4);
    }

    [Fact]
    public void ExternalNarration_WithASeparateViewpoint_IsExpressible()
    {
        // Third-person limited: the mode rev 2's character-only narrator could not express.
        var frame = Fiction(
            active: null,
            narrator: new FrameNarrator(NarratorKind.external, null, "sailor", NarrativePerson.third));

        var v4 = PlanV4Codec.CompactV4(Plan(frame));

        Assert.Contains("narrator = external (third person, following the sailor)", v4);
        Assert.Contains("you-play = (narrating)", v4);
    }

    [Fact]
    public void OneParticipant_MayControlSeveralCharacters_AndNpcsAreListedSeparately()
    {
        var frame = Fiction(characters:
        [
            new FrameCharacter("keeper", "the lighthouse keeper", Companion),
            new FrameCharacter("gull", "the gull", Companion),
            new FrameCharacter("sailor", "the sailor", User),
            new FrameCharacter("storm", "the storm"),
        ]);

        var v4 = PlanV4Codec.CompactV4(Plan(frame));

        Assert.Empty(PlanV4Codec.ValidateFrame(Plan(frame)));
        Assert.Contains("you-play = the lighthouse keeper", v4);
        Assert.Contains("they-play = the gull, the sailor", v4);
        Assert.Contains("also-in-scene = the storm", v4);
    }

    [Fact]
    public void ABoundary_IsNamedOnTheWire_SoTheMouthCanObeyIt()
    {
        var frame = Fiction(boundaries:
            [new FrameBoundaryRef("fb-1", "no third-person narration", Guid.NewGuid().ToString())]);

        Assert.Contains("boundary = no third-person narration", PlanV4Codec.CompactV4(Plan(frame)));
    }

    // ---- structural validation -----------------------------------------------------------

    [Fact]
    public void RealMode_IsOnlyLegalAsTheExitTurn()
    {
        var illegal = new Frame { Mode = FrameMode.real, Transition = FrameTransition.@continue };

        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(illegal)),
            e => e.Contains("mode=real is only legal with transition=exit"));
    }

    [Theory]
    [InlineData(FrameTransition.@continue)]
    [InlineData(FrameTransition.switchScene)]
    public void ContinueAndSwitch_RequireAScene(FrameTransition transition)
    {
        var frame = Fiction(transition) with { SceneRef = null };

        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(frame)), e => e.Contains("requires sceneRef"));
    }

    [Fact]
    public void DuplicateCharacterIds_AreRejected_ButRepeatedControllersAreNot()
    {
        var dupIds = Fiction(characters:
        [
            new FrameCharacter("keeper", "the keeper", Companion),
            new FrameCharacter("keeper", "the other keeper", User),
        ]);
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(dupIds)), e => e.Contains("duplicate characterId"));

        var sharedController = Fiction(characters:
        [
            new FrameCharacter("keeper", "the keeper", Companion),
            new FrameCharacter("gull", "the gull", Companion),
        ]);
        Assert.DoesNotContain(PlanV4Codec.ValidateFrame(Plan(sharedController)),
            e => e.Contains("duplicate") || e.Contains("controlledBy"));
    }

    [Fact]
    public void AnUnknownController_IsRejected()
    {
        var frame = Fiction(characters:
            [new FrameCharacter("keeper", "the keeper", "usr-nobody")]) with
            { ActiveCompanionCharacterId = null };

        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(frame)),
            e => e.Contains("controlledBy unknown participant"));
    }

    [Fact]
    public void TheActiveCompanionCharacter_MustResolveAndBeHers()
    {
        var missing = Fiction(active: "ghost");
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(missing)),
            e => e.Contains("is not in characters"));

        var notHers = Fiction(active: "sailor");
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(notHers)),
            e => e.Contains("is not controlled by the companion"));
    }

    [Fact]
    public void NarratorKind_DecidesWhetherACharacterIdIsRequiredOrForbidden()
    {
        var characterWithout = Fiction(narrator: new FrameNarrator(NarratorKind.character));
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(characterWithout)),
            e => e.Contains("kind=character requires characterId"));

        var externalWith = Fiction(narrator: new FrameNarrator(NarratorKind.external, "keeper"));
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(externalWith)),
            e => e.Contains("kind=external must not carry a characterId"));

        var unknownViewpoint = Fiction(
            narrator: new FrameNarrator(NarratorKind.external, null, "ghost"));
        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(unknownViewpoint)),
            e => e.Contains("viewpointCharacterId 'ghost' is not in characters"));
    }

    [Fact]
    public void ABoundaryWithoutEvidence_IsRejected()
    {
        var frame = Fiction(boundaries: [new FrameBoundaryRef("fb-1", "no third person")]);

        Assert.Contains(PlanV4Codec.ValidateFrame(Plan(frame)), e => e.Contains("has no evidenceRef"));
    }

    // ---- characters are not principals ----------------------------------------------------

    [Fact]
    public void ACharacterUsedAsAnAudiencePrincipal_IsRejected()
    {
        var plan = Plan(Fiction(), items: [new PlanItem
        {
            Id = "i1", Type = "aside", Category = RenderCategory.claim,
            Policy = ExpressionPolicy.background_only, Source = "procedure",
            Text = "Only for the sailor.", Audience = ["sailor"],
        }]);

        Assert.Contains(PlanV4Codec.ValidateFrame(plan),
            e => e.Contains("used as an audience principal"));
    }

    [Fact]
    public void ACharacterUsedAsAnItemOwner_IsRejected()
    {
        var plan = Plan(Fiction(), items: [new PlanItem
        {
            Id = "i1", Type = "aside", Category = RenderCategory.claim,
            Policy = ExpressionPolicy.background_only, Source = "procedure",
            Text = "The keeper's own thought.", Owner = "keeper",
        }]);

        Assert.Contains(PlanV4Codec.ValidateFrame(plan), e => e.Contains("used as an item owner"));
    }

    [Fact]
    public void AFrameNeverAltersAParticipantIdentity()
    {
        var plan = Plan(Fiction());

        // Authorization is not a costume: the principals are exactly what they were.
        Assert.Equal([User, Companion], plan.Participants.Select(p => p.Id));
        Assert.DoesNotContain(plan.Participants, p => p.Id is "keeper" or "sailor");
    }

    // ---- no content classification ---------------------------------------------------------

    [Fact]
    public void TheFrameCarriesNoContentClassificationAtAll()
    {
        var names = typeof(Frame).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        // Sexual content, profanity, romance, darkness and violence are ordinary fictional
        // content. There is nowhere in this record to mark them, and there must not be.
        Assert.DoesNotContain(names, n => n.Contains("rating") || n.Contains("contentclass")
            || n.Contains("intensity") || n.Contains("severity") || n.Contains("explicit")
            || n.Contains("mature") || n.Contains("nsfw"));
    }

    [Fact]
    public void ExplicitFictionalContent_ValidatesAndSerializesLikeAnyOtherScene()
    {
        var plan = Plan(Fiction(), items: [new PlanItem
        {
            Id = "i1", Type = "scene", Category = RenderCategory.state,
            Policy = ExpressionPolicy.must_express, Source = "procedure",
            Text = "They fall into bed together, and neither of them is shy about it.",
        }]);

        Assert.Empty(PlanV4Codec.ValidateFrame(plan));
        var v4 = PlanV4Codec.CompactV4(plan);
        Assert.Contains("neither of them is shy about it", v4);
    }
}
