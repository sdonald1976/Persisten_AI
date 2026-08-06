using System.Threading.Channels;

namespace Companion.Api;

/// <summary>
/// Bridges the pull-based turn (<c>IAgent.HandleAsync</c> reports chunks synchronously as they
/// arrive) to a push-based async reader (SSE / WebSocket writers). The turn writes into an
/// unbounded channel so <c>Report</c> never blocks; the transport drains it and flushes each chunk.
/// </summary>
public sealed class TokenChannelSink : IProgress<string>
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public ChannelReader<string> Reader => _channel.Reader;

    public void Report(string value) => _channel.Writer.TryWrite(value);

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
