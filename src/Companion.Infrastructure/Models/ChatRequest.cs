using Companion.Core.Abstractions;

namespace Companion.Infrastructure.Models;

/// <summary>Builds an OpenAI-compatible chat request body, adding sampling params only when configured.</summary>
internal static class ChatRequest
{
    public static Dictionary<string, object?> Build(
        EndpointOptions options, object messages, bool stream, ResponseFormat? format = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            ["messages"] = messages,
        };
        // Token usage has to be asked for when streaming; a streamed response omits it otherwise.
        // Not asking left the WebSocket path — the one a person actually talks to her through —
        // with no record of prompt size at all: every streamed reply stored a null PromptTokens,
        // so the one number that would say whether the prompt overflowed the model's window was
        // missing from exactly the path where it mattered.
        if (stream)
            body["stream_options"] = new { include_usage = true };
        if (options.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options.MaxTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
        // Stops her continuing the context packet's structure instead of answering. Never sent for
        // JSON roles, whose configuration leaves this empty.
        if (options.Stop is { Length: > 0 } stop)
            body["stop"] = stop;
        // Anti-repetition levers — the fix for small local models that loop / repeat themselves.
        if (options.FrequencyPenalty is { } frequencyPenalty)
            body["frequency_penalty"] = frequencyPenalty;
        if (options.PresencePenalty is { } presencePenalty)
            body["presence_penalty"] = presencePenalty;
        // Structured output. `json_object` is only a hint that the reply should be JSON — every
        // field's vocabulary is still the model's to invent, which is how one fact arrived under
        // two different predicates and the store kept both. `json_schema` is enforced by the
        // provider during decoding (Ollama ≥ 0.5, LM Studio, OpenAI), so an off-enum value is not
        // rejected after the fact — it cannot be generated. Providers that don't recognize it fall
        // back to their own JSON handling, which is no worse than where we were.
        if (format is { Schema: not null })
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = format.SchemaName ?? "response", schema = format.Schema, strict = true },
            };
        }
        else if (format is not null)
        {
            body["response_format"] = new { type = "json_object" };
        }
        return body;
    }
}
