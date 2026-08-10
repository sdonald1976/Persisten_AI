using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>
/// Raised when a model provider call fails in a way the caller should surface. Carries the
/// mapped, human-actionable message; the original cause is the inner exception.
/// </summary>
public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Shared hardening for the OpenAI-compatible providers: a hard response-size cap, provider-specific
/// error mapping (auth / missing-model / rate-limit / server), clean cancellation-vs-timeout
/// distinction, and redacted per-call diagnostics (role, model, latency, outcome, correlation id —
/// never prompts, content, or credentials). Transient-failure retries live in the IHttpClientFactory
/// pipeline (see <see cref="ProviderHttpClients"/>), so this layer sees only the final response.
/// Model output is always treated as untrusted.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>
    /// Invokes <paramref name="send"/> (whose handler pipeline has already applied retries) and maps
    /// the outcome. Returns a success response the caller owns/disposes; a non-2xx becomes a mapped
    /// <see cref="ModelProviderException"/>. Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>; a request timeout maps to a clear timeout error.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        EndpointOptions options, string role, ILogger logger, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await send(ct);

            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "model call ok: role={Role} model={Model} status={Status} latency={Latency}ms cid={Cid}",
                    role, options.Model, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, correlationId);
                return response;
            }

            var mapped = await MapErrorAsync(response, options, ct);
            logger.LogWarning(
                "model call failed: role={Role} model={Model} status={Status} latency={Latency}ms cid={Cid}",
                role, options.Model, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, correlationId);
            response.Dispose();
            throw new ModelProviderException(mapped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine caller cancellation — never swallow or remap
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            var timedOut = ex is TaskCanceledException or OperationCanceledException;
            logger.LogWarning(
                "model call error: role={Role} model={Model} latency={Latency}ms cid={Cid} err={Err}",
                role, options.Model, stopwatch.ElapsedMilliseconds, correlationId, ex.GetType().Name);
            throw new ModelProviderException(
                timedOut
                    ? $"The {role} model at {options.BaseUrl} (model '{options.Model}') timed out after {options.TimeoutSeconds}s."
                    : $"Couldn't reach the {role} model at {options.BaseUrl} (model '{options.Model}'). Is the server running and the model pulled/loaded?",
                ex);
        }
    }

    /// <summary>Reads a JSON body with a hard size cap, so a huge/hostile response can't exhaust memory.</summary>
    public static async Task<T?> ReadCappedJsonAsync<T>(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        // Trust the declared length when present.
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new ModelProviderException($"The model response was too large ({declared} bytes > cap {maxBytes}).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ModelProviderException($"The model response exceeded the {maxBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(buffer, JsonOptions, ct);
    }

    /// <summary>Reads a binary body with a hard size cap, so a huge/hostile response can't exhaust memory.</summary>
    public static async Task<byte[]> ReadCappedBytesAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new ModelProviderException($"The model response was too large ({declared} bytes > cap {maxBytes}).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ModelProviderException($"The model response exceeded the {maxBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Maps a non-transient failure status to a specific, actionable message (body snippet included, capped).</summary>
    private static async Task<string> MapErrorAsync(HttpResponseMessage response, EndpointOptions options, CancellationToken ct)
    {
        var snippet = await SafeSnippetAsync(response, ct);
        var where = $"{options.BaseUrl} (model '{options.Model}')";
        return (int)response.StatusCode switch
        {
            401 or 403 => $"The model server at {where} rejected the credentials (HTTP {(int)response.StatusCode}). Check the ApiKey.",
            404 => $"The model '{options.Model}' was not found at {options.BaseUrl} (HTTP 404). Pull/load it or fix the model name.{snippet}",
            429 => $"The model server at {where} is rate-limiting (HTTP 429) and retries were exhausted.{snippet}",
            >= 500 => $"The model server at {where} returned a server error (HTTP {(int)response.StatusCode}).{snippet}",
            _ => $"The model server at {where} returned HTTP {(int)response.StatusCode}.{snippet}",
        };
    }

    private static async Task<string> SafeSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = new byte[512];
            await using var s = await response.Content.ReadAsStreamAsync(ct);
            var n = await s.ReadAsync(bytes, ct);
            if (n <= 0) return "";
            var text = Encoding.UTF8.GetString(bytes, 0, n).Replace('\n', ' ').Trim();
            return string.IsNullOrWhiteSpace(text) ? "" : $" Server said: {text}";
        }
        catch
        {
            return "";
        }
    }
}
