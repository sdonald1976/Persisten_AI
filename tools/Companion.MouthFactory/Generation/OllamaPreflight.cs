using System.Text;
using System.Text.Json;

namespace Companion.MouthFactory.Generation;

public sealed record PreflightResult(bool Healthy, string Detail, string? Action = null);

/// <summary>
/// Is the GPU service actually able to generate, right now?
///
/// This exists because of a specific failure: after a training run took the GPU underneath it,
/// Ollama kept answering /api/version instantly while /api/generate hung forever. `ollama ps`
/// still reported the model resident at "100% GPU" while nvidia-smi showed 147 MiB. Nothing was
/// down; the runner was wedged, and an unattended overnight generation would have sat there
/// producing nothing until its timeout, then recorded every unit as a generator failure.
///
/// A version check cannot detect that. Only asking the service to generate something can, so
/// this issues a real, tiny completion against the model that will actually be used.
///
/// It never kills anything. A wedged runner is usually wedged BECAUSE another job holds the GPU,
/// and killing an unrelated training run to unblock a data-generation job would trade a delay
/// for the loss of hours of work. It reports and refuses instead.
/// </summary>
public static class OllamaPreflight
{
    public static async Task<PreflightResult> CheckAsync(
        HttpClient http, string baseUrl, string model, CancellationToken ct = default)
    {
        var root = baseUrl.Replace("/v1", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

        // 1. Is it there at all?
        try
        {
            using var version = await http.GetAsync($"{root}/api/version", ct);
            if (!version.IsSuccessStatusCode)
                return new PreflightResult(false, $"the service answered {(int)version.StatusCode}",
                    "Start Ollama, then re-run.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PreflightResult(false, "the service is unreachable",
                "Start Ollama (`ollama serve`), then re-run.");
        }

        // 2. Does it have the model? A missing tag is a different problem with a different fix.
        try
        {
            using var tags = await http.GetAsync($"{root}/api/tags", ct);
            var body = await tags.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var served = doc.RootElement.TryGetProperty("models", out var models)
                         && models.EnumerateArray().Any(m =>
                             m.TryGetProperty("name", out var n) && n.GetString() is { } s
                             && (s.Equals(model, StringComparison.OrdinalIgnoreCase)
                                 || (!model.Contains(':')
                                     && s.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase))));
            if (!served)
                return new PreflightResult(false, $"'{model}' is not served",
                    $"ollama pull {model}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PreflightResult(false, "the model catalog could not be read",
                "Check Ollama, then re-run.");
        }

        // 3. THE ONE THAT MATTERS: can it actually generate? A wedged runner fails here and
        //    nowhere earlier. Deliberately tiny, and on a short leash - a healthy service
        //    answers a 4-token completion quickly even from cold.
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                model,
                prompt = "ok",
                stream = false,
                options = new { num_predict = 4 },
            });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(240));
            using var response = await http.PostAsync(
                $"{root}/api/generate",
                new StringContent(payload, Encoding.UTF8, "application/json"), cts.Token);
            if (!response.IsSuccessStatusCode)
                return new PreflightResult(false,
                    $"a test generation returned {(int)response.StatusCode}",
                    "Check the Ollama log, then re-run.");
        }
        catch (OperationCanceledException)
        {
            return new PreflightResult(false,
                "a 4-token test generation did not return within 240s - the GPU runner is WEDGED "
                + "(this happens after another process takes the GPU; the service still answers "
                + "/api/version and still reports the model resident)",
                "Restart Ollama once whatever holds the GPU has finished. Nothing was killed.");
        }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            return new PreflightResult(false, "a test generation failed to connect",
                "Restart Ollama, then re-run.");
        }

        return new PreflightResult(true, $"'{model}' generated a test completion");
    }
}
