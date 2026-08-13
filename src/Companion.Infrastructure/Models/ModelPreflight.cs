using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Models;

/// <summary>One configured role, and whether the provider actually has the model it names.</summary>
/// <param name="Present">True/false once probed; null when the provider couldn't be reached.</param>
public sealed record ModelRoleStatus(string Role, string Model, string BaseUrl, bool? Present);

/// <summary>
/// The result of asking each configured provider which models it actually serves. Held as a
/// singleton so <c>/health</c> can report it, and applied to the capability registry so
/// <c>/capabilities</c> stops asserting availability it has never checked.
/// </summary>
public sealed class ModelPreflightReport
{
    public static readonly ModelPreflightReport NotRun = new();

    public DateTimeOffset? CheckedAt { get; init; }
    public IReadOnlyList<ModelRoleStatus> Roles { get; init; } = Array.Empty<ModelRoleStatus>();

    /// <summary>Roles whose model the provider definitively does not have — these will 503 on use.</summary>
    [JsonIgnore]
    public IReadOnlyList<ModelRoleStatus> Missing =>
        Roles.Where(r => r.Present == false).ToList();

    /// <summary>Roles we couldn't check because the provider was unreachable.</summary>
    [JsonIgnore]
    public IReadOnlyList<ModelRoleStatus> Unchecked =>
        Roles.Where(r => r.Present is null).ToList();
}

/// <summary>
/// Holds the latest preflight result for readers that aren't the probe (currently <c>/health</c>).
/// Registered as a singleton; written once when the startup probe finishes.
/// </summary>
public sealed class ModelPreflightState
{
    private volatile ModelPreflightReport _report = ModelPreflightReport.NotRun;

    public ModelPreflightReport Report
    {
        get => _report;
        set => _report = value;
    }
}

/// <summary>
/// Asks each configured OpenAI-compatible provider for its model list (<c>GET /models</c>, which
/// Ollama and LM Studio both serve) and reports which configured roles name a model the server
/// doesn't have.
///
/// This exists because a model name is the one piece of configuration nothing else validates: a
/// typo or an un-pulled tag builds clean, starts clean, passes every test, and then fails on the
/// user's first sentence with a 503. Checking once at startup turns that into a single actionable
/// log line — and keeps the capability registry from claiming a model works when it isn't installed.
/// </summary>
public sealed class ModelPreflight
{
    /// <summary>
    /// Probing must never delay startup behind a hung provider, so it gets its own short budget
    /// rather than the roles' generous (300s) inference timeouts.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly ModelOptions _models;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<ModelPreflight> _logger;

    public ModelPreflight(ModelOptions models, IHttpClientFactory clients, ILogger<ModelPreflight> logger)
    {
        _models = models;
        _clients = clients;
        _logger = logger;
    }

    public async Task<ModelPreflightReport> RunAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!_models.UsesRealModel)
            return ModelPreflightReport.NotRun;

        // Role → endpoint. Only the roles served by the language-model provider: the audio
        // endpoints are a separate, optional server whose absence is already reported honestly
        // (their capabilities seed as Untested), and probing it would cry wolf when it's simply
        // not running.
        var roles = new (string Role, EndpointOptions Endpoint)[]
        {
            (ProviderHttpClients.Conversation, _models.Chat),
            (ProviderHttpClients.Extraction, _models.ExtractionOrChat),
            (ProviderHttpClients.Summarizer, _models.SummarizerOrChat),
            (ProviderHttpClients.Reranker, _models.RerankerOrSummarizer),
            (ProviderHttpClients.Safety, _models.SafetyOrExtraction),
            (ProviderHttpClients.TaskAuditor, _models.TaskAuditorOrSummarizer),
            (ProviderHttpClients.ToolPlanner, _models.ToolPlannerOrExtraction),
            (ProviderHttpClients.Embeddings, _models.Embeddings),
        };
        if (_models.Vision is { } vision)
            roles = [.. roles, (ProviderHttpClients.Vision, vision)];

        // One probe per distinct server, not per role — the roster usually shares a base URL.
        var catalogs = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.OrdinalIgnoreCase);
        var statuses = new List<ModelRoleStatus>();

        foreach (var (role, endpoint) in roles)
        {
            if (!catalogs.TryGetValue(endpoint.BaseUrl, out var catalog))
            {
                catalog = await FetchCatalogAsync(role, endpoint, ct);
                catalogs[endpoint.BaseUrl] = catalog;
            }

            bool? present = catalog is null ? null : Serves(catalog, endpoint.Model);
            statuses.Add(new ModelRoleStatus(role, endpoint.Model, endpoint.BaseUrl, present));
        }

        var report = new ModelPreflightReport { CheckedAt = now, Roles = statuses };
        Report(report);
        return report;
    }

    private void Report(ModelPreflightReport report)
    {
        foreach (var group in report.Unchecked.GroupBy(r => r.BaseUrl))
        {
            _logger.LogWarning(
                "Could not reach the model server at {BaseUrl} to verify its models; {Count} role(s) unverified.",
                group.Key, group.Count());
        }

        if (report.Missing.Count == 0)
        {
            if (report.Unchecked.Count == 0)
                _logger.LogInformation("Model preflight: all {Count} configured models are present.", report.Roles.Count);
            return;
        }

        // One line per missing model (not per role) — a single wrong tag usually breaks several roles.
        foreach (var group in report.Missing.GroupBy(r => (r.Model, r.BaseUrl)))
        {
            // Each placeholder name must appear once: the logger binds them positionally, so a
            // repeated name consumes another argument rather than reusing the earlier value.
            _logger.LogError(
                "Model '{Model}' is configured for {Roles} but the server at {BaseUrl} does not have it — "
                + "those calls will fail until you pull it or correct the configured name. {Hint}",
                group.Key.Model,
                string.Join(", ", group.Select(r => r.Role)),
                group.Key.BaseUrl,
                $"Try: ollama pull {group.Key.Model}");
        }
    }

    private async Task<IReadOnlySet<string>?> FetchCatalogAsync(
        string role, EndpointOptions endpoint, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            var client = _clients.CreateClient(ProviderHttpClients.Name(role));
            using var response = await client.GetAsync("models", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Model catalog probe returned HTTP {Status} from {BaseUrl}.",
                    (int)response.StatusCode, endpoint.BaseUrl);
                return null;
            }

            var catalog = await ProviderHttp.ReadCappedJsonAsync<ModelListResponse>(
                response, endpoint.MaxResponseBytes, timeout.Token);
            var ids = catalog?.Data?
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // An empty or shapeless catalog is not evidence of absence — treat it as "unknown"
            // rather than condemning every configured model.
            return ids is { Count: > 0 } ? ids : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Model catalog probe failed for {BaseUrl}.", endpoint.BaseUrl);
            return null;
        }
    }

    /// <summary>
    /// Ollama reports tagged ids ("llama3.1:8b", "nomic-embed-text:latest") while configuration
    /// often omits an implied ":latest", so an untagged name matches its :latest tag and vice versa.
    /// </summary>
    internal static bool Serves(IReadOnlySet<string> catalog, string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return false;
        if (catalog.Contains(configured))
            return true;
        if (!configured.Contains(':'))
            return catalog.Contains(configured + ":latest");
        return configured.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            && catalog.Contains(configured[..^":latest".Length]);
    }

    private sealed class ModelListResponse
    {
        public List<ModelListEntry>? Data { get; set; }
    }

    private sealed class ModelListEntry
    {
        public string? Id { get; set; }
    }
}
