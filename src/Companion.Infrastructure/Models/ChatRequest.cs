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
        if (options.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options.MaxTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
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
