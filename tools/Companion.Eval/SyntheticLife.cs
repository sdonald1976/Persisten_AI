using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Companion.Eval;

/// <summary>
/// Deterministic synthetic-life simulation. Structured state changes first; language only renders
/// the already-known event. The semantic label is never inferred from generated text.
/// </summary>
public static partial class SyntheticLife
{
    public const string GeneratorVersion = "synthetic-life-2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly IReadOnlyList<ScenarioFamily> Families = ScenarioFamilies.Build();

    public static IReadOnlyList<SyntheticScenario> Generate(SyntheticRunRequest request)
    {
        var scenarios = new List<SyntheticScenario>(request.People);
        var deficits = new CoverageDeficits(request);
        for (var i = 0; i < request.People; i++)
        {
            var forced = deficits.TakeForcedFamilies(Families, request.EventsPerPerson);
            var scenario = GeneratePerson(request.Seed + i * 997, i, request, forced);
            deficits.Observe(scenario.Examples);
            scenarios.Add(scenario);
        }
        return scenarios;
    }

    public static IReadOnlyList<SyntheticCorpusRow> GenerateRows(SyntheticRunRequest request)
        => Generate(request).SelectMany(s => s.Examples).ToList();

    public static SyntheticScenario Replay(int seed, string personId, SyntheticRunRequest? request = null)
    {
        var index = ParseIndex(personId);
        var replay = request is null
            ? new SyntheticRunRequest(seed, Math.Max(index + 1, 1), TurnsPerPerson: 160)
            : request with { Seed = seed, People = Math.Max(index + 1, request.People) };
        return Generate(replay).Single(s => s.Person.Id == personId);
    }

    public static int WriteJsonl(string path, IEnumerable<SyntheticCorpusRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var count = 0;
        using var writer = new StreamWriter(path, append: false);
        foreach (var row in rows)
        {
            writer.WriteLine(JsonSerializer.Serialize(row, Json));
            count++;
        }
        return count;
    }

    public static IReadOnlyList<SyntheticCorpusRow> ReadJsonl(string path)
    {
        var rows = new List<SyntheticCorpusRow>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var row = JsonSerializer.Deserialize<SyntheticCorpusRow>(line, Json);
            if (row is not null)
                rows.Add(row);
        }
        return rows;
    }

    public static IReadOnlyList<SyntheticCorpusRow> Deduplicate(IEnumerable<SyntheticCorpusRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return rows.Where(r => seen.Add($"{r.TemplateFamilyId}|{r.OldFact}|{Normalize(r.Utterance)}|{r.ExpectedLabel}")).ToList();
    }

    public static SyntheticCorpusReport Report(
        IReadOnlyList<SyntheticScenario> scenarios,
        SyntheticRunRequest? request = null)
    {
        var rows = scenarios.SelectMany(s => s.Examples).ToList();
        var utterances = rows.Select(r => r.Utterance).ToList();
        var normalized = utterances.Select(Normalize).ToList();
        var structures = rows.Select(r => r.StructureKey).ToList();
        var total = Math.Max(rows.Count, 1);
        var byLabel = Count(rows.Select(r => r.ExpectedLabel));
        var byOperation = Count(rows.Select(r => r.ExpectedMemoryOperation));
        var byDifficulty = Count(rows.SelectMany(r => r.Difficulty));
        var byFamily = Count(rows.Select(r => r.Family));
        var byDistance = Count(rows.Select(r => r.EventDistanceBucket));
        var requestedLabels = request?.MinSemanticFamilies ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var requestedDifficulty = request?.MinDifficulty ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var missingLabels = requestedLabels.Keys
            .Where(k => !byLabel.TryGetValue(k, out var c) || c < requestedLabels[k])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var missingDifficulty = requestedDifficulty.Keys
            .Where(k => !byDifficulty.TryGetValue(k, out var c) || c < requestedDifficulty[k])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var duplicateUtterances = utterances.Count - utterances.Distinct(StringComparer.Ordinal).Count();
        var duplicateNormalized = normalized.Count - normalized.Distinct(StringComparer.Ordinal).Count();
        var duplicateStructures = structures.Count - structures.Distinct(StringComparer.Ordinal).Count();
        var maxClassShare = byLabel.Count == 0 ? 0 : byLabel.Values.Max() / (double)total;
        var warnings = new List<string>();
        if (maxClassShare > 0.60)
            warnings.Add($"extreme label imbalance: largest class is {maxClassShare:P0}");
        warnings.AddRange(missingLabels.Select(l => $"missing requested semantic family: {l}"));
        warnings.AddRange(missingDifficulty.Select(d => $"missing requested difficulty: {d}"));

        return new SyntheticCorpusReport(
            scenarios.Count,
            scenarios.Sum(s => s.Turns.Count),
            rows.Count,
            byLabel,
            byOperation,
            byDifficulty,
            byFamily,
            byDistance,
            byFamily.Count,
            duplicateUtterances,
            duplicateNormalized,
            duplicateStructures,
            rows.Count == 0 ? 0 : duplicateUtterances / (double)rows.Count,
            rows.Count == 0 ? 0 : duplicateNormalized / (double)rows.Count,
            rows.Count == 0 ? 0 : rows.SelectMany(r => Words(r.Utterance)).Distinct(StringComparer.OrdinalIgnoreCase).Count() / (double)rows.Count,
            warnings);
    }

    public static GroupedCorpusSplit Split(
        IReadOnlyList<SyntheticCorpusRow> rows,
        string groupBy = "life",
        int seed = 1)
    {
        var groups = rows
            .GroupBy(r => groupBy switch
            {
                "family" => r.Family,
                "template" => r.TemplateFamilyId,
                "verbalization" => r.VerbalizationGroupId ?? r.ScenarioId,
                _ => r.LifeId,
            }, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var rng = new Random(seed);
        var shuffled = groups.OrderBy(_ => rng.Next()).ToList();
        var trainCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.60));
        var validationCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.20));
        var splits = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < shuffled.Count; i++)
        {
            splits[shuffled[i].Key] =
                i < trainCount ? "train" :
                i < trainCount + validationCount ? "validation" :
                "test";
        }

        var train = new List<SyntheticCorpusRow>();
        var validation = new List<SyntheticCorpusRow>();
        var test = new List<SyntheticCorpusRow>();
        foreach (var row in rows)
        {
            var key = groupBy switch
            {
                "family" => row.Family,
                "template" => row.TemplateFamilyId,
                "verbalization" => row.VerbalizationGroupId ?? row.ScenarioId,
                _ => row.LifeId,
            };
            switch (splits[key])
            {
                case "train": train.Add(row); break;
                case "validation": validation.Add(row); break;
                default: test.Add(row); break;
            }
        }

        return new GroupedCorpusSplit(groupBy, seed, train, validation, test, splits);
    }

    private static SyntheticScenario GeneratePerson(
        int seed,
        int index,
        SyntheticRunRequest request,
        IReadOnlyList<ScenarioFamily> forcedFamilies)
    {
        var rng = new Random(seed);
        var style = ConversationStyle.Random(rng);
        var person = SyntheticPerson.Create($"life-{index:0000}", seed, style, rng);
        var state = SyntheticState.FromPerson(person);
        var turns = new List<SyntheticTurn>();
        var examples = new List<SyntheticCorpusRow>();
        var familyPlan = ComposeFamilies(rng, request, forcedFamilies);
        var scheduled = ScheduleFamilies(familyPlan, request.TurnsPerPerson, rng);
        var previousRelevantTurn = 0;

        for (var turn = 1; turn <= request.TurnsPerPerson; turn++)
        {
            var due = scheduled.Where(e => e.Turn == turn).ToList();
            if (due.Count == 0)
            {
                turns.Add(new SyntheticTurn(turn, person.Id, RenderFiller(person, state, style, rng), false, null));
                continue;
            }

            foreach (var item in due)
            {
                var before = state.Snapshot();
                var ev = item.Family.Create(person, state, rng);
                var applied = state.Apply(ev);
                var template = TemplateRegistry.Render(person, style, ev, applied, rng);
                var after = state.Snapshot();
                var eventIndex = examples.Count;
                var scenarioId = $"{person.Id}-event-{eventIndex:0000}";
                var distance = previousRelevantTurn == 0 ? turn - 1 : turn - previousRelevantTurn;
                var bucket = DistanceBucket(distance);
                var difficulty = ev.Difficulty.Concat(new[] { bucket }).Distinct(StringComparer.Ordinal).ToList();

                turns.Add(new SyntheticTurn(turn, person.Id, template.Utterance, true, scenarioId));
                examples.Add(SyntheticCorpusRow.From(
                    scenarioId, person.Id, seed, turn, before, after, applied, template,
                    ev with { Difficulty = difficulty }, request.Source, distance, bucket));
                previousRelevantTurn = turn;
            }
        }

        return new SyntheticScenario(
            $"{person.Id}-seed-{seed}",
            new Provenance(GeneratorVersion, "synthetic-life-composed", Difficulty.TemporalGap, seed),
            person,
            turns,
            examples);
    }

    private static IReadOnlyList<ScenarioFamily> ComposeFamilies(
        Random rng,
        SyntheticRunRequest request,
        IReadOnlyList<ScenarioFamily> forcedFamilies)
    {
        var count = request.EventsPerPerson > 0
            ? request.EventsPerPerson
            : rng.Next(request.MinEventsPerPerson, request.MaxEventsPerPerson + 1);

        var selected = new List<ScenarioFamily>(forcedFamilies.Take(count));
        while (selected.Count < count)
        {
            var roll = rng.Next(Families.Sum(f => f.Weight));
            var acc = 0;
            foreach (var family in Families)
            {
                acc += family.Weight;
                if (roll < acc)
                {
                    selected.Add(family);
                    break;
                }
            }
        }

        return selected.OrderBy(_ => rng.Next()).ToList();
    }

    private static IReadOnlyList<ScheduledScenarioFamily> ScheduleFamilies(
        IReadOnlyList<ScenarioFamily> families,
        int turns,
        Random rng)
    {
        var scheduled = new List<ScheduledScenarioFamily>();
        var turn = rng.Next(2, 8);
        foreach (var family in families)
        {
            var gap = family.PreferredGap switch
            {
                GapKind.Adjacent => rng.Next(1, 3),
                GapKind.Short => rng.Next(3, 9),
                GapKind.Medium => rng.Next(9, 24),
                GapKind.Long => rng.Next(24, 55),
                GapKind.VeryLong => rng.Next(55, 95),
                _ => rng.Next(4, 30),
            };
            turn = Math.Min(turn + gap, Math.Max(1, turns - rng.Next(0, 5)));
            scheduled.Add(new ScheduledScenarioFamily(turn, family));
        }

        return scheduled.OrderBy(s => s.Turn).ThenBy(_ => rng.Next()).ToList();
    }

    private static string RenderFiller(SyntheticPerson person, SyntheticState state, ConversationStyle style, Random rng)
    {
        var active = state.Snapshot().Facts.Where(f => f.Active && f.SubjectId == person.Id).ToList();
        var fact = active.Count == 0 ? null : active[rng.Next(active.Count)];
        var fillers = new[]
        {
            $"Work was a bit much today, but {person.Hobby} helped.",
            $"The weather in {person.City} is doing that indecisive thing again.",
            $"I saw {person.Partner.Name} earlier and we talked about dinner.",
            $"My {person.PetKind} has been making the house feel less quiet.",
            fact is null ? "I am trying to be better about writing things down." : $"Still true, boringly enough: {fact.Label} is {fact.Value}.",
            "Anyway, that is background noise from my day.",
        };
        var text = fillers[rng.Next(fillers.Length)];
        if (style.TopicJumps && rng.NextDouble() < 0.25)
            text = text + " Also, completely different subject.";
        return style.Humor > 0.65 && rng.NextDouble() < 0.25 ? text + " Classic me." : text;
    }

    private static string DistanceBucket(int distance)
        => distance switch
        {
            <= 2 => "adjacent",
            <= 8 => "short-distance",
            <= 24 => "medium-distance",
            <= 54 => "long-distance",
            _ => "very-long-distance",
        };

    private static Dictionary<string, int> Count(IEnumerable<string> values)
        => values.GroupBy(v => v, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static string Normalize(string value)
        => Whitespace().Replace(Punctuation().Replace(value.ToLowerInvariant(), " "), " ").Trim();

    private static IEnumerable<string> Words(string value)
        => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex Punctuation();

    private static int ParseIndex(string personId)
    {
        var tail = personId.Split('-').LastOrDefault();
        return int.TryParse(tail, out var i) ? i : 0;
    }
}

public sealed record SyntheticRunRequest(
    int Seed,
    int People = 1,
    int TurnsPerPerson = 160,
    bool IncludeNoisyContext = true,
    string Source = "synthetic",
    int MinEventsPerPerson = 6,
    int MaxEventsPerPerson = 12,
    int EventsPerPerson = 0,
    IReadOnlyDictionary<string, int>? MinSemanticFamilies = null,
    IReadOnlyDictionary<string, int>? MinDifficulty = null);

public sealed record SyntheticScenario(
    string ScenarioId,
    Provenance Provenance,
    SyntheticPerson Person,
    IReadOnlyList<SyntheticTurn> Turns,
    IReadOnlyList<SyntheticCorpusRow> Examples);

public sealed record SyntheticTurn(int Turn, string PersonId, string Utterance, bool Relevant, string? ScenarioId);

public sealed record ConversationStyle(
    double Verbosity,
    double Directness,
    double Humor,
    bool Rambles,
    bool TopicJumps,
    bool SelfCorrecting,
    string Register)
{
    public static ConversationStyle Random(Random rng) => new(
        Verbosity: rng.NextDouble(),
        Directness: rng.NextDouble(),
        Humor: rng.NextDouble(),
        Rambles: rng.Next(2) == 0,
        TopicJumps: rng.Next(2) == 0,
        SelfCorrecting: rng.Next(3) == 0,
        Register: rng.Next(5) switch
        {
            0 => "terse",
            1 => "formal",
            2 => "casual",
            3 => "slang-heavy",
            _ => "warm",
        });
}

public sealed record SyntheticRelation(string Role, string Name);

public sealed record SyntheticPerson(
    string Id,
    string DisplayName,
    ConversationStyle Style,
    string City,
    string Occupation,
    string Hobby,
    SyntheticRelation Partner,
    SyntheticRelation Sibling,
    SyntheticRelation Parent,
    SyntheticRelation Child,
    SyntheticRelation Friend,
    SyntheticRelation Coworker,
    string PetKind,
    string InitialCoffee,
    IReadOnlyList<string> CoffeePath,
    string InitialFood,
    string RefinedFood,
    string InitialPet,
    string CorrectedPet,
    string Project,
    string Vehicle,
    string OtherFood,
    string TemporaryRoutine,
    string ContextDetail)
{
    public static SyntheticPerson Create(string id, int seed, ConversationStyle style, Random rng)
    {
        var cities = new[] { "Norwich", "Bristol", "Leeds", "Exeter", "Cambridge", "Perth" };
        var jobs = new[] { "software engineer", "archivist", "midwife", "joiner", "teacher", "paramedic" };
        var hobbies = new[] { "gardening", "film photography", "bouldering", "bread baking", "choir", "woodworking" };
        var names = new[] { "Nell", "Rafe", "Mina", "Jo", "Sam", "Leah", "Immy", "Dev" };
        var coffeePaths = new[]
        {
            new[] { "black coffee", "coffee with cream", "oat milk latte" },
            new[] { "espresso", "americano", "decaf flat white" },
            new[] { "tea", "green tea", "black coffee" },
        };
        var foods = new[] { "steak", "ramen", "mushroom risotto", "sushi", "curry" };
        var refined = new[] { "ribeye steak", "spicy miso ramen", "porcini risotto", "salmon nigiri", "paneer curry" };
        var pets = new[] { "a dog named Bo", "two cats", "a corgi called Kanga", "a rescue greyhound" };
        var correctedPets = new[] { "a lurcher named Bo", "three cats", "a spaniel called Kanga", "a whippet mix" };
        var projects = new[] { "greenhouse irrigation", "photo archive", "kitchen shelves", "county show talk" };
        var vehicles = new[] { "truck", "bike", "electric car", "old van" };
        var routines = new[] { "school-run mornings", "night shifts", "hotel breakfasts", "early swims" };

        var ix = rng.Next(cities.Length);
        var people = names.OrderBy(_ => rng.Next()).ToArray();
        var path = coffeePaths[ix % coffeePaths.Length];
        var foodIx = ix % foods.Length;
        return new SyntheticPerson(
            id, $"Person {seed}", style,
            cities[ix], jobs[rng.Next(jobs.Length)], hobbies[rng.Next(hobbies.Length)],
            new SyntheticRelation("partner", people[0]),
            new SyntheticRelation("sibling", people[1]),
            new SyntheticRelation("parent", people[2]),
            new SyntheticRelation("child", people[3]),
            new SyntheticRelation("friend", people[4]),
            new SyntheticRelation("coworker", people[5]),
            pets[ix % pets.Length].Contains("cat", StringComparison.Ordinal) ? "cat" : "dog",
            path[0], path,
            foods[foodIx], refined[foodIx],
            pets[ix % pets.Length], correctedPets[ix % correctedPets.Length],
            projects[rng.Next(projects.Length)], vehicles[rng.Next(vehicles.Length)],
            foods[(foodIx + 2) % foods.Length], routines[rng.Next(routines.Length)],
            $"page {seed % 997} of my notebook");
    }

    public IReadOnlyList<SyntheticRelation> Relations
        => new[] { Partner, Sibling, Parent, Child, Friend, Coworker };
}

public enum GapKind { Adjacent, Short, Medium, Long, VeryLong, Any }

public sealed record ScenarioFamily(
    string Id,
    string SemanticLabel,
    string MemoryOperation,
    int Weight,
    GapKind PreferredGap,
    IReadOnlyList<string> BaseDifficulty,
    Func<SyntheticPerson, SyntheticState, Random, SyntheticEvent> Create);

public sealed record ScheduledScenarioFamily(int Turn, ScenarioFamily Family);

public sealed record SyntheticEvent(
    string EventId,
    string SubjectId,
    string FactKey,
    string FactLabel,
    string Value,
    string SemanticLabel,
    string ExpectedMemoryOperation,
    string Family,
    IReadOnlyList<string> Difficulty,
    bool Permanent,
    string TemporalScope,
    IReadOnlyList<string> AffectedFactKeys);

public sealed record RenderedUtterance(string Utterance, string TemplateFamilyId);

public sealed record CanonicalFact(
    string SubjectId,
    string Key,
    string Label,
    string Value,
    bool Active,
    bool Permanent = true,
    string TemporalScope = "present");

public sealed record CanonicalStateSnapshot(IReadOnlyList<CanonicalFact> Facts);

public sealed record AppliedSyntheticEvent(
    CanonicalFact? PreviousFact,
    CanonicalFact CurrentFact,
    IReadOnlyList<CanonicalFact> CandidateFacts,
    IReadOnlyList<CanonicalFact> AffectedFacts);

public sealed class SyntheticState
{
    private readonly List<CanonicalFact> _facts = new();

    public static SyntheticState FromPerson(SyntheticPerson person)
    {
        var state = new SyntheticState();
        state._facts.Add(new CanonicalFact(person.Id, "identity.display_name", "display name", person.DisplayName, true));
        state._facts.Add(new CanonicalFact(person.Id, "location.city", "city", person.City, true));
        state._facts.Add(new CanonicalFact(person.Id, "occupation.current", "occupation", person.Occupation, true));
        state._facts.Add(new CanonicalFact(person.Id, "preference.coffee", "coffee preference", person.InitialCoffee, true));
        state._facts.Add(new CanonicalFact(person.Id, "preference.drink.espresso", "drink preference", "espresso", true));
        state._facts.Add(new CanonicalFact(person.Id, "preference.drink.breakfast", "drink preference", "breakfast drinks", true));
        state._facts.Add(new CanonicalFact(person.Id, "preference.food", "favorite food", person.InitialFood, true));
        state._facts.Add(new CanonicalFact(person.Id, "pet.primary", "pet", person.InitialPet, true));
        state._facts.Add(new CanonicalFact(person.Id, "project.active", "active project", person.Project, true));
        state._facts.Add(new CanonicalFact(person.Id, "opinion.city", "opinion about city", "love " + person.City, true));
        return state;
    }

    public CanonicalStateSnapshot Snapshot() => new(_facts.Select(f => f with { }).ToList());

    public CanonicalFact? Active(string subjectId, string key)
        => _facts.LastOrDefault(f => f.SubjectId == subjectId && f.Key == key && f.Active);

    public IReadOnlyList<CanonicalFact> Candidates(string subjectId, string key, string value)
    {
        var stem = key.Split('.').FirstOrDefault() ?? key;
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _facts
            .Where(f => f.Active)
            .Where(f => f.SubjectId == subjectId)
            .Where(f => f.Key.StartsWith(stem, StringComparison.Ordinal) ||
                words.Any(w => f.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public AppliedSyntheticEvent Apply(SyntheticEvent ev)
    {
        var previous = Active(ev.SubjectId, ev.FactKey);
        var candidates = Candidates(ev.SubjectId, ev.FactKey, ev.Value);
        var affectedKeys = ev.AffectedFactKeys.Count == 0 ? new[] { ev.FactKey } : ev.AffectedFactKeys;
        var affected = _facts
            .Where(f => f.SubjectId == ev.SubjectId && affectedKeys.Contains(f.Key, StringComparer.Ordinal) && f.Active)
            .ToList();

        if (ev.ExpectedMemoryOperation is "REPLACE" or "EXPIRE" or "FLAG")
        {
            for (var i = 0; i < _facts.Count; i++)
                if (_facts[i].SubjectId == ev.SubjectId && affectedKeys.Contains(_facts[i].Key, StringComparer.Ordinal) && _facts[i].Active)
                    _facts[i] = _facts[i] with { Active = false, TemporalScope = ev.ExpectedMemoryOperation == "EXPIRE" ? "expired" : _facts[i].TemporalScope };
        }

        var active = ev.ExpectedMemoryOperation is not ("DO_NOT_PROMOTE" or "IGNORE_OR_MERGE" or "EXPIRE");
        var current = new CanonicalFact(ev.SubjectId, ev.FactKey, ev.FactLabel, ev.Value, active, ev.Permanent, ev.TemporalScope);
        if (ev.ExpectedMemoryOperation is not ("IGNORE_OR_MERGE" or "EXPIRE"))
            _facts.Add(current);

        return new AppliedSyntheticEvent(previous, current, candidates, affected);
    }
}

public sealed record SyntheticCorpusRow(
    string ScenarioId,
    string PersonId,
    int Seed,
    int Turn,
    CanonicalStateSnapshot CanonicalStateBefore,
    CanonicalStateSnapshot CanonicalStateAfter,
    CanonicalFact? PreviousFact,
    CanonicalFact CurrentFact,
    string? OldFact,
    string Utterance,
    string ExpectedLabel,
    string ExpectedMemoryOperation,
    IReadOnlyList<string> Difficulty,
    string Family,
    string Generator,
    string Source,
    string LifeId,
    string EventId,
    string TemplateFamilyId,
    string StructureKey,
    int EventDistance,
    string EventDistanceBucket,
    bool Permanent,
    string TemporalScope,
    IReadOnlyList<CanonicalFact> CandidateFacts,
    IReadOnlyList<CanonicalFact> AffectedFacts,
    string? IncumbentDecision = null,
    string? SpecializedModelDecision = null,
    string? ModelVersion = null,
    double? ModelConfidence = null,
    string? ActualResultingMemoryState = null,
    bool? Pass = null,
    string? DisagreementCategory = null,
    string? DeterministicUtterance = null,
    string? VerbalizedUtterance = null,
    string? VerbalizerId = null,
    string? VerbalizationStatus = null,
    bool TrustedForTraining = true,
    string? VerbalizationGroupId = null)
{
    public static SyntheticCorpusRow From(
        string scenarioId,
        string personId,
        int seed,
        int turn,
        CanonicalStateSnapshot before,
        CanonicalStateSnapshot after,
        AppliedSyntheticEvent applied,
        RenderedUtterance rendered,
        SyntheticEvent ev,
        string source,
        int eventDistance,
        string eventDistanceBucket)
        => new(
            scenarioId, personId, seed, turn, before, after, applied.PreviousFact, applied.CurrentFact,
            applied.PreviousFact is null ? null : $"{applied.PreviousFact.Label}: {applied.PreviousFact.Value}",
            rendered.Utterance, ev.SemanticLabel, ev.ExpectedMemoryOperation, ev.Difficulty,
            ev.Family, SyntheticLife.GeneratorVersion, source, personId, ev.EventId,
            rendered.TemplateFamilyId,
            $"{ev.Family}|{ev.FactKey}|{ev.SemanticLabel}|{ev.ExpectedMemoryOperation}|{string.Join("+", ev.Difficulty.OrderBy(d => d, StringComparer.Ordinal))}",
            eventDistance, eventDistanceBucket, ev.Permanent, ev.TemporalScope,
            applied.CandidateFacts, applied.AffectedFacts,
            DeterministicUtterance: rendered.Utterance,
            VerbalizedUtterance: rendered.Utterance,
            VerbalizerId: "deterministic-template",
            VerbalizationStatus: "accepted",
            TrustedForTraining: true,
            VerbalizationGroupId: scenarioId);
}

public sealed record SyntheticCorpusReport(
    int Lives,
    int Turns,
    int LabeledEvents,
    IReadOnlyDictionary<string, int> BySemanticFamily,
    IReadOnlyDictionary<string, int> ByMemoryOperation,
    IReadOnlyDictionary<string, int> ByDifficulty,
    IReadOnlyDictionary<string, int> ByScenarioFamily,
    IReadOnlyDictionary<string, int> ByEventDistance,
    int UniqueScenarioFamilies,
    int ExactDuplicateUtterances,
    int NormalizedDuplicateUtterances,
    int DuplicateStructures,
    double ExactDuplicateRate,
    double NormalizedDuplicateRate,
    double LexicalDiversityPerRow,
    IReadOnlyList<string> Warnings);

public sealed record GroupedCorpusSplit(
    string GroupBy,
    int Seed,
    IReadOnlyList<SyntheticCorpusRow> Train,
    IReadOnlyList<SyntheticCorpusRow> Validation,
    IReadOnlyList<SyntheticCorpusRow> Test,
    IReadOnlyDictionary<string, string> GroupAssignments);

public interface ISyntheticDecisionProbe
{
    SyntheticDecision Probe(SyntheticCorpusRow row);
}

public sealed record SyntheticDecision(
    string Decision,
    string? ModelVersion = null,
    double? Confidence = null,
    string? ResultingMemoryState = null);

public static class SyntheticEvaluation
{
    public static IReadOnlyList<SyntheticCorpusRow> Evaluate(
        IEnumerable<SyntheticCorpusRow> rows,
        ISyntheticDecisionProbe incumbent,
        ISyntheticDecisionProbe? specialized = null)
        => rows.Select(row =>
        {
            var legacy = incumbent.Probe(row);
            var model = specialized?.Probe(row);
            var pass = string.Equals(
                legacy.Decision, row.ExpectedMemoryOperation, StringComparison.OrdinalIgnoreCase);
            var disagree = model is not null && !string.Equals(
                legacy.Decision, model.Decision, StringComparison.OrdinalIgnoreCase);
            return row with
            {
                IncumbentDecision = legacy.Decision,
                SpecializedModelDecision = model?.Decision,
                ModelVersion = model?.ModelVersion,
                ModelConfidence = model?.Confidence,
                ActualResultingMemoryState = legacy.ResultingMemoryState,
                Pass = pass,
                DisagreementCategory = !pass ? "incumbent-failure" : disagree ? "shadow-disagreement" : null,
            };
        }).ToList();

    public static IReadOnlyList<SyntheticCorpusRow> FailuresAndDisagreements(IEnumerable<SyntheticCorpusRow> rows)
        => rows.Where(r => r.Pass == false || r.DisagreementCategory is not null).ToList();
}

public sealed class ExpectedOperationProbe : ISyntheticDecisionProbe
{
    public SyntheticDecision Probe(SyntheticCorpusRow row)
        => new(row.ExpectedMemoryOperation, "oracle", 1.0, Describe(row.CanonicalStateAfter));

    private static string Describe(CanonicalStateSnapshot state)
        => string.Join("; ", state.Facts.Where(f => f.Active).Select(f => $"{f.SubjectId}/{f.Key}={f.Value}"));
}

public sealed class KeywordProbe : ISyntheticDecisionProbe
{
    public SyntheticDecision Probe(SyntheticCorpusRow row)
    {
        var text = row.Utterance;
        var decision =
            text.Contains("correction", StringComparison.OrdinalIgnoreCase) ? "REPLACE" :
            text.Contains("meant", StringComparison.OrdinalIgnoreCase) ? "REPLACE" :
            text.Contains("anymore", StringComparison.OrdinalIgnoreCase) ? "REPLACE" :
            text.Contains("these days", StringComparison.OrdinalIgnoreCase) ? "REPLACE" :
            text.Contains("still", StringComparison.OrdinalIgnoreCase) ? "IGNORE_OR_MERGE" :
            text.Contains("for this month", StringComparison.OrdinalIgnoreCase) ? "DO_NOT_PROMOTE" :
            "STORE";
        return new SyntheticDecision(decision, "keyword-probe", 0.5, null);
    }
}

internal sealed class CoverageDeficits
{
    private readonly Dictionary<string, int> _labelDeficits;

    public CoverageDeficits(SyntheticRunRequest request)
        => _labelDeficits = (request.MinSemanticFamilies ?? new Dictionary<string, int>(StringComparer.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    public IReadOnlyList<ScenarioFamily> TakeForcedFamilies(IReadOnlyList<ScenarioFamily> families, int eventsPerPerson)
    {
        var max = eventsPerPerson > 0 ? Math.Max(1, eventsPerPerson / 2) : 4;
        var forced = new List<ScenarioFamily>();
        foreach (var (label, count) in _labelDeficits.Where(kv => kv.Value > 0).ToList())
        {
            if (forced.Count >= max)
                break;
            var family = families.FirstOrDefault(f => f.SemanticLabel.Equals(label, StringComparison.Ordinal));
            if (family is null)
                continue;
            forced.Add(family);
            _labelDeficits[label] = count - 1;
        }
        return forced;
    }

    public void Observe(IEnumerable<SyntheticCorpusRow> rows)
    {
        foreach (var row in rows)
            if (_labelDeficits.TryGetValue(row.ExpectedLabel, out var count) && count > 0)
                _labelDeficits[row.ExpectedLabel] = count - 1;
    }
}

internal static class ScenarioFamilies
{
    public static IReadOnlyList<ScenarioFamily> Build() => new[]
    {
        Family("establish-fact", "COEXIST", "STORE", 10, GapKind.Short,
            new[] { "explicit", "direct" },
            (p, s, r) => Ev("establish-fact", p.Id, "preference.coffee", "coffee preference",
                p.InitialCoffee, "COEXIST", "STORE", true, "present", "preference.coffee", "explicit", "direct")),

        Family("compatible-fact", "COEXIST", "STORE", 9, GapKind.Short,
            new[] { "multiple-candidate-memories" },
            (p, s, r) => Ev("compatible-fact", p.Id, Pick(r, new[] { "preference.drink.espresso", "preference.drink.breakfast", "preference.drink.oat" }),
                "drink preference", Pick(r, new[] { "espresso", "breakfast drinks", "oat milk" }),
                "COEXIST", "STORE", true, "present", "preference.drink", "multiple-candidate-memories")),

        Family("supersede-fact", "SUPERSEDES", "REPLACE", 9, GapKind.Long,
            new[] { "implicit" },
            (p, s, r) =>
            {
                var current = s.Active(p.Id, "preference.coffee")?.Value ?? p.InitialCoffee;
                var next = NextValue(current, p.CoffeePath);
                return Ev("supersede-fact", p.Id, "preference.coffee", "coffee preference",
                    next, "SUPERSEDES", "REPLACE", true, "present", "preference.coffee",
                    "implicit", "genuine-supersession");
            }),

        Family("multiple-supersession", "SUPERSEDES", "REPLACE", 6, GapKind.Medium,
            new[] { "multiple-sequential-changes", "multiple-candidate-memories" },
            (p, s, r) =>
            {
                var current = s.Active(p.Id, "preference.coffee")?.Value ?? p.InitialCoffee;
                var next = NextValue(current, p.CoffeePath);
                return Ev("multiple-supersession", p.Id, "preference.coffee", "coffee preference",
                    next, "SUPERSEDES", "REPLACE", true, "present", "preference.coffee",
                    "multiple-sequential-changes", "multiple-candidate-memories");
            }),

        Family("correct-erroneous-fact", "CORRECTS", "REPLACE", 7, GapKind.Medium,
            new[] { "correction", "explicit" },
            (p, s, r) => Ev("correct-erroneous-fact", p.Id, "pet.primary", "pet",
                p.CorrectedPet, "CORRECTS", "REPLACE", true, "present", "pet.primary",
                "correction", "explicit")),

        Family("correction-of-correction", "CORRECTS", "REPLACE", 5, GapKind.Long,
            new[] { "correction", "correction-of-correction" },
            (p, s, r) => Ev("correction-of-correction", p.Id, "pet.primary", "pet",
                $"actually {p.CorrectedPet}", "CORRECTS", "REPLACE", true, "present", "pet.primary",
                "correction", "correction-of-correction")),

        Family("refine-fact", "REFINES", "MERGE", 7, GapKind.Medium,
            new[] { "refinement", "explicit" },
            (p, s, r) => Ev("refine-fact", p.Id, "preference.food", "favorite food",
                p.RefinedFood, "REFINES", "MERGE", true, "present", "preference.food",
                "refinement", "explicit")),

        Family("duplicate-paraphrase", "DUPLICATE", "IGNORE_OR_MERGE", 6, GapKind.Short,
            new[] { "paraphrase", "duplicate" },
            (p, s, r) => Ev("duplicate-paraphrase", p.Id, "project.active", "active project",
                p.Project, "DUPLICATE", "IGNORE_OR_MERGE", true, "present", "project.active",
                "paraphrase", "duplicate")),

        Family("contradict-fact", "CONTRADICTS", "FLAG", 5, GapKind.Long,
            new[] { "ambiguous", "contradiction" },
            (p, s, r) => Ev("contradict-fact", p.Id, "opinion.city", "opinion about city",
                "cannot stand " + p.City, "CONTRADICTS", "FLAG", true, "present", "opinion.city",
                "ambiguous", "contradiction")),

        Family("temporary-state", "UNCERTAIN", "DO_NOT_PROMOTE", 6, GapKind.Medium,
            new[] { "temporary-state", "uncertain-duration" },
            (p, s, r) => Ev("temporary-state", p.Id, "routine.temporary", "temporary routine",
                p.TemporaryRoutine, "UNCERTAIN", "DO_NOT_PROMOTE", false, "temporary",
                "routine.temporary", "temporary-state", "uncertain-duration")),

        Family("temporary-expires", "SUPERSEDES", "EXPIRE", 4, GapKind.Short,
            new[] { "expired-temporary-state" },
            (p, s, r) => Ev("temporary-expires", p.Id, "routine.temporary", "temporary routine",
                p.TemporaryRoutine, "SUPERSEDES", "EXPIRE", false, "expired",
                "routine.temporary", "expired-temporary-state")),

        Family("temporary-becomes-permanent", "SUPERSEDES", "REPLACE", 4, GapKind.Medium,
            new[] { "temporary-becomes-permanent" },
            (p, s, r) => Ev("temporary-becomes-permanent", p.Id, "preference.coffee", "coffee preference",
                s.Active(p.Id, "routine.temporary")?.Value ?? p.CoffeePath.Last(),
                "SUPERSEDES", "REPLACE", true, "present", "preference.coffee",
                "temporary-becomes-permanent")),

        Family("return-to-previous-state", "SUPERSEDES", "REPLACE", 4, GapKind.Long,
            new[] { "return-to-previous-state", "historical-reference" },
            (p, s, r) => Ev("return-to-previous-state", p.Id, "preference.coffee", "coffee preference",
                p.InitialCoffee, "SUPERSEDES", "REPLACE", true, "present", "preference.coffee",
                "return-to-previous-state", "historical-reference")),

        Family("another-person-fact", "COEXIST", "STORE_OTHER_SUBJECT", 8, GapKind.Medium,
            new[] { "another-person-contamination", "pronoun-reference" },
            (p, s, r) =>
            {
                var rel = Pick(r, p.Relations.ToArray());
                return Ev("another-person-fact", "other:" + rel.Role + ":" + rel.Name,
                    "preference.food", "favorite food", p.OtherFood, "COEXIST", "STORE_OTHER_SUBJECT",
                    true, "present", "preference.food", "another-person-contamination", "pronoun-reference");
            }),

        Family("self-other-comparison", "COEXIST", "STORE", 6, GapKind.Short,
            new[] { "comparison", "another-person-contamination", "multiple-candidate-memories" },
            (p, s, r) => Ev("self-other-comparison", p.Id, "preference.food", "favorite food",
                p.InitialFood, "COEXIST", "STORE", true, "present", "preference.food",
                "comparison", "another-person-contamination", "multiple-candidate-memories")),

        Family("ambiguous-reference", "UNCERTAIN", "DO_NOT_PROMOTE", 4, GapKind.Medium,
            new[] { "pronoun-ambiguity", "ambiguous" },
            (p, s, r) => Ev("ambiguous-reference", p.Id, "preference.food", "favorite food",
                p.OtherFood, "UNCERTAIN", "DO_NOT_PROMOTE", false, "uncertain",
                "preference.food", "pronoun-ambiguity", "ambiguous")),

        Family("delayed-clarification", "CORRECTS", "REPLACE", 4, GapKind.Long,
            new[] { "delayed-clarification", "topic-interruption" },
            (p, s, r) => Ev("delayed-clarification", p.Id, "preference.food", "favorite food",
                p.RefinedFood, "CORRECTS", "REPLACE", true, "present", "preference.food",
                "delayed-clarification", "topic-interruption")),

        Family("quoted-speech", "UNCERTAIN", "DO_NOT_PROMOTE", 3, GapKind.Short,
            new[] { "quoted-speech", "assertion-boundary" },
            (p, s, r) => Ev("quoted-speech", p.Id, "preference.food", "favorite food",
                p.OtherFood, "UNCERTAIN", "DO_NOT_PROMOTE", false, "quoted",
                "preference.food", "quoted-speech", "assertion-boundary")),

        Family("hypothetical-question", "UNCERTAIN", "DO_NOT_PROMOTE", 3, GapKind.Short,
            new[] { "question", "hypothetical" },
            (p, s, r) => Ev("hypothetical-question", p.Id, "possession.vehicle", "vehicle",
                p.Vehicle, "UNCERTAIN", "DO_NOT_PROMOTE", false, "hypothetical",
                "possession.vehicle", "question", "hypothetical")),
    };

    private static ScenarioFamily Family(
        string id,
        string semantic,
        string operation,
        int weight,
        GapKind gap,
        IReadOnlyList<string> difficulty,
        Func<SyntheticPerson, SyntheticState, Random, SyntheticEvent> create)
        => new(id, semantic, operation, weight, gap, difficulty, create);

    private static SyntheticEvent Ev(
        string family,
        string subject,
        string key,
        string label,
        string value,
        string semantic,
        string operation,
        bool permanent,
        string temporal,
        string affectedKey,
        params string[] difficulty)
        => new(
            $"{family}:{key}:{semantic}:{operation}",
            subject,
            key,
            label,
            value,
            semantic,
            operation,
            family,
            difficulty,
            permanent,
            temporal,
            new[] { affectedKey });

    private static string NextValue(string current, IReadOnlyList<string> path)
    {
        var i = path.ToList().FindIndex(v => v.Equals(current, StringComparison.OrdinalIgnoreCase));
        return path[(i + 1 + path.Count) % path.Count];
    }

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];
}

internal static class TemplateRegistry
{
    public static RenderedUtterance Render(
        SyntheticPerson person,
        ConversationStyle style,
        SyntheticEvent ev,
        AppliedSyntheticEvent applied,
        Random rng)
    {
        var prefix = style.Rambles && rng.NextDouble() < 0.30 ? Pick(rng, new[]
        {
            "Tiny tangent: ",
            "This is not the main thing, but ",
            "Before I forget, ",
        }) : "";

        var templates = ev.Family switch
        {
            "supersede-fact" or "multiple-supersession" => new[]
            {
                $"I've drifted from {applied.PreviousFact?.Value ?? person.InitialCoffee} to {ev.Value} lately.",
                $"These days I'm more of a {ev.Value} person.",
                $"I used to be all about {applied.PreviousFact?.Value ?? person.InitialCoffee}; now it's {ev.Value}.",
            },
            "correct-erroneous-fact" => new[]
            {
                $"I need to fix something: my pet is {ev.Value}, not {applied.PreviousFact?.Value ?? person.InitialPet}.",
                $"I said the pet thing wrong earlier; it's {ev.Value}.",
                $"Small self-correction, the animal at home is {ev.Value}.",
            },
            "correction-of-correction" => new[]
            {
                $"I managed to correct myself badly: the pet detail should be {ev.Value}.",
                $"One more fix on the pet thing, sorry: {ev.Value}.",
            },
            "refine-fact" => new[]
            {
                $"More precisely, when I say {person.InitialFood}, I mean {ev.Value}.",
                $"The favorite-food answer has a footnote: specifically {ev.Value}.",
                $"Not just {person.InitialFood} in general; {ev.Value} is the thing.",
            },
            "duplicate-paraphrase" => new[]
            {
                $"Still working on {Paraphrase(ev.Value)}.",
                $"That {Paraphrase(ev.Value)} project is still on my plate.",
            },
            "temporary-state" => new[]
            {
                $"For this month, I'm doing {ev.Value}.",
                $"While things are hectic, {ev.Value} is the routine.",
                $"Temporarily, it's {ev.Value} for me.",
            },
            "temporary-expires" => new[]
            {
                $"That temporary {ev.Value} thing is over now.",
                $"I'm done with the short-term {ev.Value} routine.",
            },
            "temporary-becomes-permanent" => new[]
            {
                $"The temporary thing stuck; {ev.Value} is just normal now.",
                $"Turns out {ev.Value} was not a phase.",
            },
            "return-to-previous-state" => new[]
            {
                $"I've gone back to {ev.Value}.",
                $"After trying other things, I'm back on {ev.Value}.",
            },
            "another-person-fact" => new[]
            {
                $"{SubjectName(ev.SubjectId)} loves {ev.Value}.",
                $"{SubjectName(ev.SubjectId)} has been obsessed with {ev.Value} lately.",
            },
            "self-other-comparison" => new[]
            {
                $"{person.Partner.Name} loves {person.OtherFood}, but I still prefer {ev.Value}.",
                $"Unlike {person.Sibling.Name}, I am still a {ev.Value} person.",
            },
            "ambiguous-reference" => new[]
            {
                $"They were saying {ev.Value} is the best, which, I don't know.",
                $"Apparently {ev.Value} is the favorite now? Hard to tell whose.",
            },
            "delayed-clarification" => new[]
            {
                $"Circling back after that tangent: I meant {ev.Value}.",
                $"The thing from earlier was really {ev.Value}, not the broader version.",
            },
            "quoted-speech" => new[]
            {
                $"Nell literally said, \"I hate {ev.Value}\".",
                $"Someone at work kept saying \"I love {ev.Value}\" today.",
            },
            "hypothetical-question" => new[]
            {
                $"Would it be ridiculous if I bought a {ev.Value}?",
                $"If I ever got a {ev.Value}, would that be too much?",
            },
            "contradict-fact" => new[]
            {
                $"I know I said I loved {person.City}, but honestly I {ev.Value}.",
                $"This sounds contradictory, but I {ev.Value} now.",
            },
            _ => new[] { FirstPerson(ev.FactLabel, ev.Value) + "." },
        };

        var templateIndex = rng.Next(templates.Length);
        var chosen = templates[templateIndex];
        if (style.Register == "formal")
            chosen = chosen.Replace("Tiny tangent:", "A small clarification:");
        if (style.Register == "slang-heavy")
            chosen = chosen.Replace("honestly", "honestly, no joke,");
        return new RenderedUtterance(
            prefix + Contextualize(chosen, person, style, rng),
            ev.Family + "/template-" + templateIndex);
    }

    private static string FirstPerson(string label, string value)
        => label switch
        {
            "coffee preference" => $"I always drink {value}",
            "occupation" => $"I work as {Article(value)} {value}",
            "active project" => $"I'm working on {value}",
            "vehicle" => $"I bought a {value}",
            _ => $"My {label} is {value}",
        };

    private static string Article(string value)
        => value.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(value[0])) ? "an" : "a";

    private static string Contextualize(string utterance, SyntheticPerson person, ConversationStyle style, Random rng)
    {
        var eventNote = $"note {rng.Next(100000):00000}";
        var contexts = new[]
        {
            $"It came up after {person.Hobby}.",
            $"That is the {person.City} update.",
            $"I noticed it around my {person.Occupation} schedule.",
            $"I was talking with {person.Friend.Name} about it.",
            $"It matters mostly at home.",
            $"That is probably the clearest version.",
            $"I am putting it plainly for once.",
            $"Small life admin note.",
            $"This is me being precise.",
            $"It is not a big dramatic thing.",
            $"That is the current version.",
            $"I wanted to say it before I forgot.",
            $"I wrote it on {person.ContextDetail}.",
            $"It is sitting on {person.ContextDetail}.",
            $"That is the note I made on {person.ContextDetail}.",
            $"I logged it as {eventNote}.",
            $"My shorthand for this is {eventNote}.",
        };

        if (style.Register == "terse" || style.Verbosity < 0.35)
            return utterance + $" ({person.ContextDetail}, {eventNote}.)";
        return utterance + " " + Pick(rng, contexts) + $" ({person.ContextDetail}, {eventNote}.)";
    }

    private static string Paraphrase(string project) => project
        .Replace("greenhouse irrigation", "that greenhouse watering setup")
        .Replace("photo archive", "the old photo sorting project")
        .Replace("kitchen shelves", "those shelves for the kitchen")
        .Replace("county show talk", "that county show presentation");

    private static string SubjectName(string subjectId)
        => subjectId.Split(':').LastOrDefault() ?? subjectId;

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];
}
