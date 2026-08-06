namespace Companion.Infrastructure.Models;

/// <summary>Builds an OpenAI-compatible chat request body, adding sampling params only when configured.</summary>
internal static class ChatRequest
{
    public static Dictionary<string, object?> Build(EndpointOptions options, object messages, bool stream)
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
        return body;
    }
}
