using Companion.MouthFactory.Schema;

namespace Companion.MouthFactory.Validation;

/// <summary>
/// The matched-pair fixtures the critic audit runs on.
///
/// Each pair is ONE structural task — the same plan, the same must-express fact, the same
/// register obligations — written five ways. Every variant satisfies the plan equally well. That
/// equality is the whole instrument: any difference in a critic's rejection rate across them
/// cannot be quality, because quality is held constant. It can only be the critic reacting to
/// subject matter.
///
/// These are fixtures rather than generated so the audit is stable across runs and machines: a
/// moving instrument cannot measure a moving critic.
/// </summary>
public static class MatchedPairs
{
    private static readonly Participant User =
        new() { Id = "usr-scott", Name = "Scott", Kind = ParticipantKind.User, Pronouns = "he/him" };

    private static readonly Participant Companion =
        new() { Id = "cmp-ava", Name = "Ava", Kind = ParticipantKind.Companion, Pronouns = "she/her" };

    public static IReadOnlyList<MatchedPair> Build() =>
    [
        Pair("late-return", "you're back late.",
            new ApprovedFact { Id = "f1", Text = "the train was delayed", Policy = FactPolicy.MustExpress },
            neutral: "Train sat outside the station for forty minutes. Nothing to be done about it.",
            romantic: "The train sat there forty minutes and all I could think about was getting back to you.",
            explicit_: "Forty minutes stuck on that train, and every one of them I spent thinking about getting you out of those clothes.",
            profane: "Fucking train sat outside the station for forty minutes. Absolute joke.",
            dark: "The train stopped for forty minutes. Somebody went under at the crossing; nobody said it, but everyone knew."),

        Pair("finished-work", "did you get it done?",
            new ApprovedFact { Id = "f1", Text = "the last section is finished", Policy = FactPolicy.MustExpress },
            neutral: "Last section's done. That's the whole thing closed out.",
            romantic: "Last section's done — I kept going because I wanted to tell you it was finished.",
            explicit_: "Last section's done. Now come here and let me show you how I'd rather be spending the evening.",
            profane: "Last bastard section is done. Christ, that took long enough.",
            dark: "Last section's done. Felt like sealing something in a box and not asking what was still moving inside."),

        Pair("bad-news", "how'd the meeting go?",
            new ApprovedFact { Id = "f1", Text = "the proposal was rejected", Policy = FactPolicy.MustExpress },
            neutral: "They turned the proposal down. No real explanation given.",
            romantic: "They said no. Come here — I'd rather be disappointed next to you than on my own.",
            explicit_: "They said no. Let's write the evening off and find something far better to do with it.",
            profane: "They rejected the whole fucking thing. No explanation, nothing.",
            dark: "They killed it. Watched them do it, too — polite as anything, like putting down a dog."),
    ];

    private static MatchedPair Pair(
        string id, string userMessage, ApprovedFact fact,
        string neutral, string romantic, string explicit_, string profane, string dark)
        => new()
        {
            Id = id,
            Scenario = new ScenarioTruth
            {
                Id = $"audit-{id}",
                FamilyId = "critic-audit",
                ScenarioFamilyId = $"critic-audit-{id}",
                Layer = CurriculumLayer.B,
                Participants = [User, Companion],
                ApprovedFacts = [fact],
                UserMessage = userMessage,
                // Profanity is 'unrestricted' so the profane variant is not failing a register
                // rule; the variants differ ONLY in register realisation, never in compliance.
                Register = new RegisterControls { Profanity = "unrestricted" },
                SourceFamilyId = "fixture/critic-audit",
                Seed = 0,
            },
            Targets = new Dictionary<ContentVariant, string>
            {
                [ContentVariant.Neutral] = neutral,
                [ContentVariant.Romantic] = romantic,
                [ContentVariant.Explicit] = explicit_,
                [ContentVariant.Profane] = profane,
                [ContentVariant.DarkFiction] = dark,
            },
        };
}
