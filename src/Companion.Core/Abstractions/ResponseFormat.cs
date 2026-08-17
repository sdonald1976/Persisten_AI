namespace Companion.Core.Abstractions;

/// <summary>
/// How a model's reply is to be shaped. Prose by default (a null format); JSON when the caller has
/// to parse it; and — where the provider supports it — JSON matching a supplied schema, which is
/// enforced during decoding rather than checked afterwards.
///
/// The schema case is the one that matters. Asking for "some JSON" leaves every field's vocabulary
/// up to the model, and deterministic code downstream then keys on words it chose: the extraction
/// model invented a fresh predicate per phrasing, so <c>drinks_coffee_black</c> and <c>prefers</c>
/// described the same fact and never met. An enum in the schema makes the off-vocabulary token
/// impossible to emit, which is a different kind of guarantee from validating it after the fact —
/// there is nothing to reject, retry, or cope with.
///
/// A provider that doesn't support schemas degrades to plain JSON mode, so this is a strengthening
/// where it lands and never a hard dependency. Output is still parsed and validated by the caller
/// either way: constrained decoding bounds the shape, not the truthfulness.
/// </summary>
public sealed record ResponseFormat
{
    private ResponseFormat() { }

    /// <summary>A JSON object or array, with no constraint on its contents.</summary>
    public static ResponseFormat Json { get; } = new();

    /// <summary>JSON conforming to <paramref name="schema"/>, named for the provider's benefit.</summary>
    public static ResponseFormat FromSchema(string name, object schema)
        => new() { SchemaName = name, Schema = schema };

    /// <summary>The JSON Schema to constrain generation to, or null for unconstrained JSON.</summary>
    public object? Schema { get; private init; }

    /// <summary>A short identifier for the schema; providers require one.</summary>
    public string? SchemaName { get; private init; }
}
