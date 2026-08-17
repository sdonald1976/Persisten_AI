using System.Diagnostics;
using Companion.Core;
using Companion.Core.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;

namespace Companion.Infrastructure.Cognition;

/// <summary>
/// One loaded ONNX model, shared by everything that uses it.
///
/// Loading is the expensive part — tens to hundreds of megabytes mapped and initialized — so a
/// session is created once at startup and reused for the life of the process. <c>InferenceSession</c>
/// is documented as thread-safe for <c>Run</c>, which is what makes that possible; the surrounding
/// counters are the part that needs care, and they are interlocked rather than locked so that
/// instrumentation never becomes the contention.
///
/// Execution is pinned to the CPU deliberately. This box is a 6 GB GTX 1660 already swapping
/// between five Ollama models, where a single conversational turn takes 50–135 seconds; a 22M-param
/// encoder runs in single-digit milliseconds on a CPU core and has no business competing for that
/// VRAM. If a future model is big enough to want a GPU, that is a decision to take deliberately and
/// measure, not a default to inherit.
///
/// A failure to load is not an exception here. Specialist models improve on heuristics that still
/// exist, so the honest outcome of a missing or broken file is an unavailable model, a logged
/// reason, and the old behaviour — unless it was declared <see cref="CognitiveModelEntry.Required"/>.
/// </summary>
internal sealed class OnnxSession : IDisposable
{
    private readonly InferenceSession? _session;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;

    private long _calls;
    private long _failures;
    private long _totalTicks;

    private OnnxSession(
        string name, string? path, InferenceSession? session, string? reason, TimeSpan timeout, ILogger logger)
    {
        Name = name;
        Path = path;
        _session = session;
        Reason = reason;
        _timeout = timeout;
        _logger = logger;
    }

    public string Name { get; }
    public string? Path { get; }
    public string? Reason { get; }
    public bool IsAvailable => _session is not null;

    /// <summary>The model's declared input names, so a caller can adapt to export variations.</summary>
    public IReadOnlyCollection<string> InputNames =>
        _session?.InputMetadata.Keys.ToArray() ?? Array.Empty<string>();

    public CognitiveModelStatus Status => new()
    {
        Name = Name,
        Available = IsAvailable,
        Path = Path,
        Reason = Reason,
        Calls = Interlocked.Read(ref _calls),
        Failures = Interlocked.Read(ref _failures),
        TotalMilliseconds = TimeSpan.FromTicks(Interlocked.Read(ref _totalTicks)).TotalMilliseconds,
    };

    /// <summary>
    /// Loads a model, or returns an unavailable session explaining why not. Only throws when the
    /// entry is <see cref="CognitiveModelEntry.Required"/> — that flag is how an operator says
    /// "I would rather fail at startup than quietly get the old behaviour back".
    /// </summary>
    public static OnnxSession Load(
        string name, CognitiveModelEntry entry, string directory, TimeSpan timeout, ILogger logger)
    {
        if (!entry.Enabled)
            return new OnnxSession(name, null, null, "disabled", timeout, logger);

        var path = Resolve(entry.Path, directory);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var reason = $"file not found: {path}";
            if (entry.Required)
                throw new FileNotFoundException($"Required cognitive model '{name}' is missing.", path);
            logger.LogWarning(
                "Cognitive model {Name} is enabled but {Reason}. Falling back to the built-in " +
                "behaviour. See docs/SPECIALIST_MODELS.md for how to obtain it.", name, reason);
            return new OnnxSession(name, path, null, reason, timeout, logger);
        }

        try
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // One inference at a time per call, several calls in parallel: the turn pipeline
                // is already concurrent, and letting each inference fan out across every core just
                // makes them fight each other.
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
            };
            var session = new InferenceSession(path, options);
            logger.LogInformation(
                "Cognitive model {Name} loaded from {Path} (inputs: {Inputs}).",
                name, path, string.Join(", ", session.InputMetadata.Keys));
            return new OnnxSession(name, path, session, null, timeout, logger);
        }
        catch (Exception ex)
        {
            if (entry.Required)
                throw;
            logger.LogWarning(ex, "Cognitive model {Name} failed to load from {Path}; falling back.", name, path);
            return new OnnxSession(name, path, null, $"failed to load: {ex.Message}", timeout, logger);
        }
    }

    /// <summary>
    /// Runs the model and returns the raw output tensor, or null when it is unavailable, timed out,
    /// cancelled, or threw. Null means "no judgment" and the caller falls back — an inference
    /// failure must never take a turn down with it.
    /// </summary>
    public async Task<float[]?> RunAsync(
        IReadOnlyList<NamedOnnxValue> inputs, CancellationToken ct = default)
    {
        if (_session is null)
            return null;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);

        var started = Stopwatch.GetTimestamp();
        try
        {
            // Off the turn's thread: ONNX Run is synchronous and CPU-bound, and the caller is an
            // async pipeline that should not be blocked by it.
            var output = await Task.Run(() =>
            {
                using var results = _session.Run(inputs);
                return results.First().AsEnumerable<float>().ToArray();
            }, linked.Token);

            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _totalTicks, Stopwatch.GetTimestamp() - started);
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failures);
            _logger.LogWarning(
                "Cognitive model {Name} exceeded {Timeout}ms; falling back for this call.",
                Name, _timeout.TotalMilliseconds);
            return null;
        }
        catch (OperationCanceledException)
        {
            // The turn itself was cancelled — not this model's failure, and not worth counting.
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            _logger.LogWarning(ex, "Cognitive model {Name} failed during inference; falling back.", Name);
            return null;
        }
    }

    /// <summary>Absolute paths win; anything else is relative to the model directory.</summary>
    private static string? Resolve(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(directory, path);
    }

    public void Dispose() => _session?.Dispose();
}
