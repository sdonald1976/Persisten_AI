namespace Companion.PlanV3;

/// <summary>
/// The central assembler (P5): the ONLY thing that grants authority. Contributors
/// propose; this validates provenance, approves/downgrades/rejects each proposed policy
/// against the source's registered capability, resolves register conflicts by the
/// contract's precedence rules, applies disclosure/retention, runs the source-side
/// coaching lint, enforces recipient authorization, applies token budgets, and emits the
/// final native plan plus a content-safe report.
///
/// The authority invariants, all enforced here and adversarially tested:
///  - an UNREGISTERED source can never reach must_express / must_not_express /
///    ask_required / register restrictions; its informational items become
///    background_only (or are rejected), diagnosed, never silently promoted;
///  - `user-preference.*` requires a stored preference reference in provenance;
///  - `hosting-config.*` requires a configuration reference;
///  - `tool-authorization.*` is claimable only by the tool-authorization subsystem;
///  - `epistemic-integrity.*` only by sources that own knowledge state;
///  - promotion of background content requires BOTH a planner request and a capability
///    that permits promotion, and is always recorded.
/// </summary>
public static class PlanV3Assembler
{
    /// <summary>Sources permitted to own each privileged reason-code family.</summary>
    private static readonly Dictionary<string, string[]> FamilyOwners = new()
    {
        ["tool-authorization."] = ["tool-authorization"],
        ["epistemic-integrity."] = ["concepts", "supersession", "retrieval", "procedure"],
        ["privacy-audience."] = ["privacy", "working-context"],
        ["user-preference."] = ["user-preference"],
        ["hosting-config."] = ["hosting-config"],
    };

    /// <summary>Evidence-bearing families: authority must be referenced, not claimed.</summary>
    private static readonly string[] EvidenceRequired = ["user-preference.", "hosting-config."];

    /// <summary>Register precedence by reason family (spec §5.4), strongest first.</summary>
    private static readonly string[] RegisterPrecedence =
        ["user-preference.", "hosting-config.", "privacy-audience.",
         "tool-authorization.", "epistemic-integrity.", "persona.", "relationship.",
         "mood.", "working-context.", "mirror."];

    public static AssemblyReport Assemble(
        PlanContributionContext context,
        IReadOnlyList<IPlanV3Contributor> contributors,
        IReadOnlyDictionary<string, SourceCapability> capabilities,
        PlanV3 seed,
        int? maxItems = null)
    {
        var items = new List<PlanItem>(seed.Items);
        var outcomes = new List<ContributionOutcome>();
        var violations = new List<string>();
        var failures = new List<string>();
        var lintRejections = new List<string>();
        var provenance = new List<string>();
        var registerVotes = new List<(RegisterProposal Vote, string Source, bool Registered)>();
        var restrictions = new List<RegisterRestriction>(seed.RegisterRestrictions ?? []);
        string? askItemId = seed.Question.ItemId;
        var n = 0;

        foreach (var contributor in contributors)
        {
            PlanContributionResult result;
            try
            {
                result = contributor.Contribute(context);
            }
            catch (Exception ex)
            {
                // One organ failing must not break the plan or the other organs.
                failures.Add($"{contributor.SourceId}: {ex.GetType().Name}");
                continue;
            }
            if (result.Error is { } err)
            {
                failures.Add($"{contributor.SourceId}: {Sanitize(err)}");
                continue;
            }

            var registered = capabilities.TryGetValue(contributor.SourceId, out var cap);
            provenance.Add($"contributor:{contributor.SourceId}{(registered ? "" : "(unregistered)")}");

            foreach (var vote in result.Register ?? [])
                registerVotes.Add((vote, contributor.SourceId, registered));

            foreach (var proposal in result.Items)
            {
                var (decision, granted, reason) = Adjudicate(
                    contributor.SourceId, cap, registered, proposal, violations);

                outcomes.Add(new ContributionOutcome(
                    contributor.SourceId, proposal.LocalId, decision, reason,
                    proposal.ProposedPolicy, granted));

                if (granted is not { } policy)
                    continue;

                var id = $"{contributor.SourceId[..Math.Min(3, contributor.SourceId.Length)]}{++n}";
                var item = new PlanItem
                {
                    Id = id,
                    Type = proposal.Type,
                    Category = proposal.Category,
                    Policy = policy,
                    Text = proposal.Text,
                    Quoted = proposal.Quoted,
                    Value = proposal.Value,
                    Source = contributor.SourceId,
                    Provenance = proposal.Provenance
                        ?? new Provenance(Origin: cap?.DefaultOrigin ?? "derived"),
                    Confidence = proposal.Confidence,
                    Validity = proposal.Validity,
                    ReasonCode = proposal.ReasonCode,
                    Classification = proposal.Classification ?? Classification.personal,
                    Disclosure = proposal.Disclosure ?? cap?.DefaultDisclosure ?? Disclosure.participants,
                    Owner = proposal.Owner,
                    Audience = proposal.Audience,
                    Retention = context.SensitiveTurn
                        ? MostRestrictive(proposal.Retention ?? cap?.DefaultRetention ?? Retention.full)
                        : proposal.Retention ?? cap?.DefaultRetention ?? Retention.full,
                    Priority = proposal.Priority,
                };

                // Source-side lint at assembly: authored coaching never enters the plan.
                if (PlanV3Codec.CoachingViolation(item) is { } lint)
                {
                    lintRejections.Add($"{item.Id} source={item.Source} rule=producer-coaching");
                    outcomes[^1] = outcomes[^1] with { Decision = "rejected", Reason = "coaching-lint" };
                    continue;
                }

                if (policy == ExpressionPolicy.ask_required)
                    askItemId ??= id;

                items.Add(item);
            }
        }

        // ---- register resolution: precedence by reason family, losers recorded --------
        var (register, decisions, registerRestrictions) =
            ResolveRegister(seed.Register, registerVotes, capabilities, violations);
        restrictions.AddRange(registerRestrictions);

        // ---- budget: obligations are undroppable; optional/background shed by priority
        if (maxItems is { } cap2 && items.Count > cap2)
        {
            var keep = items.Where(IsObligation).ToList();
            var droppable = items.Except(keep)
                .OrderByDescending(i => i.Policy == ExpressionPolicy.may_express)
                .ThenByDescending(i => i.Priority ?? 0)
                .ToList();
            keep.AddRange(droppable.Take(Math.Max(0, cap2 - keep.Count)));
            foreach (var dropped in items.Except(keep))
                outcomes.Add(new ContributionOutcome(dropped.Source, dropped.Id, "dropped",
                    "token-budget", dropped.Policy, null));
            items = keep;
        }

        var question = items.Any(i => i.Policy == ExpressionPolicy.ask_required)
            ? new QuestionPolicyBlock(QuestionPolicy.ask_required,
                askItemId ?? items.First(i => i.Policy == ExpressionPolicy.ask_required).Id)
            : seed.Question;

        var plan = seed with
        {
            Items = items,
            Register = register,
            RegisterRestrictions = restrictions.Count > 0 ? restrictions : null,
            Question = question,
        };

        return new AssemblyReport
        {
            Plan = plan,
            Outcomes = outcomes,
            RegisterDecisions = decisions,
            AuthorityViolations = violations,
            ContributorFailures = failures,
            LintRejections = lintRejections,
            Provenance = provenance,
        };
    }

    private static bool IsObligation(PlanItem i) => i.Policy is ExpressionPolicy.must_express
        or ExpressionPolicy.ask_required or ExpressionPolicy.must_not_express
        or ExpressionPolicy.admit_unknown;

    private static Retention MostRestrictive(Retention proposed)
        => proposed == Retention.full ? Retention.no_telemetry_text : proposed;

    /// <summary>The authority decision for one proposal. Never throws; always explains.</summary>
    private static (string Decision, ExpressionPolicy? Granted, string? Reason) Adjudicate(
        string sourceId, SourceCapability? cap, bool registered,
        ProposedItem proposal, List<string> violations)
    {
        var privileged = proposal.ProposedPolicy is ExpressionPolicy.must_express
            or ExpressionPolicy.must_not_express or ExpressionPolicy.ask_required;

        if (!registered)
        {
            if (privileged)
            {
                violations.Add($"{sourceId}: unregistered source proposed {proposal.ProposedPolicy}");
                return ("rejected", null, "unregistered-source-privileged-policy");
            }
            // A reason code IS an authority claim; an unregistered source owns no family,
            // so the item is refused rather than admitted carrying a false claim.
            if (proposal.ReasonCode is not null)
            {
                violations.Add($"{sourceId}: unregistered source claimed reason code authority");
                return ("rejected", null, "unregistered-source-reason-code");
            }
            // Informational content from an unknown source: background pending registration.
            return ("downgraded", ExpressionPolicy.background_only, "unregistered-source-background-only");
        }

        if (!cap!.AllowedCategories.Contains(proposal.Category))
            return ("rejected", null, "category-not-permitted");

        if (proposal.Provenance?.Origin is { } origin
            && cap.AllowedOrigins.Count > 0 && !cap.AllowedOrigins.Contains(origin))
        {
            violations.Add($"{sourceId}: claimed origin '{origin}' outside capability");
            return ("rejected", null, "origin-not-permitted");
        }

        if (proposal.ReasonCode is { } code)
        {
            var family = FamilyOwners.Keys.FirstOrDefault(f => code.StartsWith(f, StringComparison.Ordinal));
            if (family is null)
                return ("rejected", null, "reason-code-outside-permitted-families");
            if (!FamilyOwners[family].Contains(sourceId))
            {
                violations.Add($"{sourceId}: claimed {family}* it does not own");
                return ("rejected", null, "reason-family-not-owned");
            }
            if (!cap.ReasonCodeFamilies.Contains(family))
                return ("rejected", null, "reason-family-not-in-capability");
            if (EvidenceRequired.Any(f => code.StartsWith(f, StringComparison.Ordinal))
                && string.IsNullOrEmpty(proposal.Provenance?.EvidenceRef))
            {
                violations.Add($"{sourceId}: {code} without an evidence reference");
                return ("rejected", null, "authority-claimed-without-evidence");
            }
        }

        if (proposal.ProposedPolicy == ExpressionPolicy.ask_required && !cap.MayProposeQuestions)
        {
            violations.Add($"{sourceId}: proposed a question without question authority");
            return ("rejected", null, "questions-not-permitted");
        }

        if (cap.AllowedPolicies.Contains(proposal.ProposedPolicy))
            return ("accepted", proposal.ProposedPolicy, null);

        // Planning promotion: cognition asked, and the capability allows being promoted.
        if (proposal.PlanningPromotion && cap.PromotableByPlanner
            && proposal.ProposedPolicy is ExpressionPolicy.must_express or ExpressionPolicy.may_express)
            return ("promoted", proposal.ProposedPolicy, "planner-authorized-promotion");

        if (privileged)
            violations.Add($"{sourceId}: proposed {proposal.ProposedPolicy} beyond capability");

        return cap.FallbackPolicy is { } fallback
            ? ("downgraded", fallback, "policy-beyond-capability")
            : ("rejected", null, "policy-beyond-capability");
    }

    /// <summary>
    /// Register resolution: every dimension goes to the strongest-precedence owned vote;
    /// restrictive values additionally require registration, restriction authority, and
    /// (for user-preference/hosting-config) an evidence reference. Losers are recorded.
    /// </summary>
    private static (RegisterVector, List<RegisterDecision>, List<RegisterRestriction>) ResolveRegister(
        RegisterVector seed,
        List<(RegisterProposal Vote, string Source, bool Registered)> votes,
        IReadOnlyDictionary<string, SourceCapability> capabilities,
        List<string> violations)
    {
        var decisions = new List<RegisterDecision>();
        var restrictions = new List<RegisterRestriction>();
        var values = new Dictionary<string, string>();

        foreach (var group in votes.GroupBy(v => v.Vote.Dimension))
        {
            var eligible = new List<(RegisterProposal Vote, string Source, int Rank)>();
            foreach (var (vote, source, registered) in group)
            {
                if (!registered)
                {
                    violations.Add($"{source}: unregistered source voted on register.{vote.Dimension}");
                    continue;
                }
                var cap = capabilities[source];
                if (!cap.MayInfluenceRegister)
                {
                    violations.Add($"{source}: register vote without register authority");
                    continue;
                }
                if (vote.Restrictive)
                {
                    if (!cap.MayProposeRegisterRestrictions)
                    {
                        violations.Add($"{source}: restrictive register value without restriction authority");
                        continue;
                    }
                    var family = FamilyOwners.Keys.FirstOrDefault(f => vote.ReasonCode.StartsWith(f, StringComparison.Ordinal));
                    if (family is null || !FamilyOwners[family].Contains(source))
                    {
                        violations.Add($"{source}: restrictive register value under unowned reason family");
                        continue;
                    }
                    if (EvidenceRequired.Any(f => vote.ReasonCode.StartsWith(f, StringComparison.Ordinal))
                        && string.IsNullOrEmpty(vote.Provenance?.EvidenceRef))
                    {
                        violations.Add($"{source}: restrictive register value without evidence reference");
                        continue;
                    }
                }
                var rank = Array.FindIndex(RegisterPrecedence, f => vote.ReasonCode.StartsWith(f, StringComparison.Ordinal));
                eligible.Add((vote, source, rank < 0 ? int.MaxValue : rank));
            }

            if (eligible.Count == 0)
                continue;

            var winner = eligible.OrderBy(e => e.Rank).First();
            values[group.Key] = winner.Vote.Value;
            decisions.Add(new RegisterDecision(
                group.Key, winner.Vote.Value, winner.Source, winner.Vote.ReasonCode,
                eligible.Where(e => e != winner).Select(e => $"{e.Source}:{e.Vote.Value}").ToList()));

            if (winner.Vote.Restrictive)
                restrictions.Add(new RegisterRestriction(
                    group.Key, winner.Vote.Value, winner.Source, winner.Vote.ReasonCode, winner.Vote.Provenance));
        }

        string? Pick(string dim, string? fallback) => values.TryGetValue(dim, out var v) ? v : fallback;
        var register = new RegisterVector
        {
            Warmth = Pick("warmth", seed.Warmth),
            Bluntness = Pick("bluntness", seed.Bluntness),
            Playfulness = Pick("playfulness", seed.Playfulness),
            Teasing = Pick("teasing", seed.Teasing),
            Skepticism = Pick("skepticism", seed.Skepticism),
            Intensity = Pick("intensity", seed.Intensity),
            Verbosity = Pick("verbosity", seed.Verbosity),
            Profanity = Pick("profanity", seed.Profanity),
            Mirror = values.TryGetValue("mirror", out var m) ? m == "true" : seed.Mirror,
            LegacyStyle = seed.LegacyStyle,
        };
        return (PlanV3Codec.Canonicalize(register), decisions, restrictions);
    }

    private static string Sanitize(string error)
        => error.Length <= 80 ? error : error[..80];
}
