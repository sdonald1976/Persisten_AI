namespace Companion.Infrastructure.Models;

/// <summary>Builds an OpenAI-compatible chat request body, adding sampling params only when configured.</summary>
internal static class ChatRequest
{
    public static Dictionary<string, object?> Build(
        EndpointOptions options, object messages, bool stream, bool jsonMode = false)
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
        // OpenAI-compatible structured-output hint; Ollama and LM Studio both honor json_object.
        if (jsonMode)
            body["response_format"] = new { type = "json_object" };
        return body;
    }
}
