using System.Text.Json;
using System.Text.Json.Nodes;
using Companion.PlanV3;
using Xunit;

namespace Companion.Tests;

/// <summary>
/// Cross-checks `response-plan-v4.schema.json` against the code that actually validates and
/// serializes plans. Two documents describing one contract drift the moment nobody compares
/// them, and the drift is invisible until a producer writes a field no consumer reads.
///
/// No JSON-Schema validator is referenced by this solution, so the cross-check is structural:
/// the schema's property names, enum members and required lists are compared against the C#
/// types and the codec's own rules.
/// </summary>
public class PlanV4SchemaTests
{
    private static JsonNode Schema()
    {
        var path = Path.Combine(RepoRoot(), "docs", "response-plan-v4.schema.json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static JsonNode V3Schema()
    {
        var path = Path.Combine(RepoRoot(), "docs", "response-plan-v3.schema.json");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    private static JsonObject Props(JsonNode node, string path)
    {
        var current = node;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = current![step];
        return current!.AsObject();
    }

    private static string[] Enum(JsonNode node, string path)
        => Props(node, path)["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    // ---- the sibling relationship ---------------------------------------------------------

    [Fact]
    public void V4IsV3PlusFrame_AndV3IsUntouched()
    {
        var v3 = Props(V3Schema(), "properties").Select(p => p.Key).ToList();
        var v4 = Props(Schema(), "properties").Select(p => p.Key).ToList();

        Assert.Equal(v3.Concat(["frame"]).OrderBy(x => x), v4.OrderBy(x => x));
        // plan/3 stays frozen: it neither knows about the frame nor loosens its field set.
        Assert.DoesNotContain("frame", v3);
        Assert.False(V3Schema()["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void TheProtocolConstMatchesTheCodec()
    {
        Assert.Equal(PlanV4Codec.Protocol, Props(Schema(), "properties/protocol")["const"]!.GetValue<string>());
        Assert.Equal("plan/3", Props(V3Schema(), "properties/protocol")["const"]!.GetValue<string>());
    }

    [Fact]
    public void EveryObjectInTheFrameIsClosed()
    {
        // additionalProperties:false throughout, as plan/3 has it — the field set is the
        // contract, and a producer inventing a field must fail rather than be ignored.
        foreach (var def in new[] { "frame", "frameCharacter", "frameNarrator", "frameBoundary" })
            Assert.False(Props(Schema(), $"$defs/{def}")["additionalProperties"]!.GetValue<bool>(), def);

        Assert.False(Schema()["additionalProperties"]!.GetValue<bool>());
    }

    // ---- schema enums match the C# enums ----------------------------------------------------

    [Theory]
    [InlineData("$defs/frame/properties/mode", typeof(FrameMode))]
    [InlineData("$defs/frame/properties/narration", typeof(FrameNarration))]
    [InlineData("$defs/frame/properties/continuity", typeof(FrameContinuity))]
    [InlineData("$defs/frameNarrator/properties/kind", typeof(NarratorKind))]
    [InlineData("$defs/frameNarrator/properties/person", typeof(NarrativePerson))]
    public void ClosedEnums_MatchTheirCsharpTypes(string path, Type enumType)
    {
        var schema = Enum(Schema(), path).OrderBy(x => x, StringComparer.Ordinal);
        var code = System.Enum.GetNames(enumType).OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(code, schema);
    }

    [Fact]
    public void TheTransitionEnum_MatchesTheWireSpellingTheCodecEmits()
    {
        // `switchScene` and `@continue` are C# spelling constraints; the wire says
        // `switch` and `continue`, and the codec's Kebab is the single place that maps them.
        var schema = Enum(Schema(), "$defs/frame/properties/transition");
        var wire = System.Enum.GetValues<FrameTransition>()
            .Select(PlanV4Codec.Kebab)
            .OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(wire, schema.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("switch", schema);
        Assert.Contains("continue", schema);
    }

    // ---- schema property sets match the C# records -------------------------------------------

    [Theory]
    [InlineData("frame", typeof(Frame))]
    [InlineData("frameCharacter", typeof(FrameCharacter))]
    [InlineData("frameNarrator", typeof(FrameNarrator))]
    [InlineData("frameBoundary", typeof(FrameBoundaryRef))]
    public void SchemaProperties_MatchTheRecordProperties(string def, Type type)
    {
        var schema = Props(Schema(), $"$defs/{def}/properties").Select(p => p.Key)
            .OrderBy(x => x, StringComparer.Ordinal);
        var code = type.GetProperties()
            .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), true)
                .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .FirstOrDefault()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(code, schema);
    }

    // ---- required lists match what the codec actually enforces ---------------------------------

    [Fact]
    public void FrameRequiredFields_AreTheOnesTheCodecCannotDefault()
    {
        var required = Props(Schema(), "$defs/frame")["required"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();

        // mode and transition have no defensible default: a frame that does not say whether
        // it is fiction, or what it is doing, is not a frame.
        Assert.Equal(["mode", "transition"], required);
    }

    [Fact]
    public void ABoundaryRequiresItsEvidence_InBothSchemaAndCode()
    {
        var required = Props(Schema(), "$defs/frameBoundary")["required"]!.AsArray()
            .Select(x => x!.GetValue<string>()).ToList();
        Assert.Contains("evidenceRef", required);

        // ...and the codec rejects one without, rather than the schema alone carrying it.
        var plan = FramePlan(new Frame
        {
            Mode = FrameMode.fiction,
            Transition = FrameTransition.enter,
            Boundaries = [new FrameBoundaryRef("fb-1", "no third person")],
        });
        Assert.Contains(PlanV4Codec.ValidateFrame(plan), e => e.Contains("no evidenceRef"));
    }

    // ---- no content classification, in the schema either -----------------------------------------

    [Fact]
    public void TheSchemaCarriesNoContentClassification()
    {
        // Scanned over the CONTRACT SURFACE — property names and enum values — not
        // descriptions. Prose scanning failed on "narrating" (contains "rating") and on
        // "Explicit. Absent means..." meaning explicitly-stated: both are English, neither
        // is a content class, and a test that cannot tell them apart teaches nothing.
        var surface = new List<string>();
        foreach (var def in new[] { "frame", "frameCharacter", "frameNarrator", "frameBoundary" })
        {
            foreach (var prop in Props(Schema(), $"$defs/{def}/properties"))
            {
                surface.Add(prop.Key.ToLowerInvariant());
                if (prop.Value?["enum"] is JsonArray values)
                    surface.AddRange(values.Select(v => v!.GetValue<string>().ToLowerInvariant()));
            }
        }

        // There is nowhere to mark sexual, profane, dark or violent content, and there must
        // not be: those are ordinary possible fictional content.
        foreach (var banned in new[] { "rating", "contentclass", "content_class", "intensity",
                                       "severity", "nsfw", "maturity", "explicitness", "adult" })
            Assert.DoesNotContain(banned, surface);

        // The surface is exactly what the contract says it is.
        Assert.Contains("mode", surface);
        Assert.Contains("fiction", surface);
        Assert.Contains("narration", surface);
    }

    private static PlanV3.PlanV3 FramePlan(Frame frame) => new()
    {
        Protocol = PlanV4Codec.Protocol,
        TraceId = Guid.NewGuid(),
        Participants =
        [
            new Participant("usr-scott", ParticipantRole.user, "Scott"),
            new Participant("companion-ava", ParticipantRole.companion, "Ava"),
        ],
        Act = "respond",
        Question = new QuestionPolicyBlock(QuestionPolicy.question_forbidden),
        Register = PlanV3Codec.Canonicalize(new RegisterVector()),
        Frame = frame,
    };
}
