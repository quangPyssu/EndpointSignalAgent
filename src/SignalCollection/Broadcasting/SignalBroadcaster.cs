using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.SignalCollection.Broadcasting;

/// <summary>
/// Broadcasts signals to multiple channel writers (SignalWriterService + FeatureExtractorService).
/// Implements pub/sub pattern to ensure both consumers receive all signals.
/// </summary>
public sealed class SignalBroadcaster : ISignalBroadcaster
{
    private readonly ILogger<SignalBroadcaster> _logger;
    private readonly List<ChannelWriter<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)>> _writers;

    public SignalBroadcaster(
        ILogger<SignalBroadcaster> logger,
        IEnumerable<ChannelWriter<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)>> writers)
    {
        _logger = logger;
        _writers = writers.ToList();
        
        if (_writers.Count == 0)
        {
            _logger.LogWarning("SignalBroadcaster initialized with no channel writers");
        }
        else
        {
            _logger.LogInformation("SignalBroadcaster initialized with {Count} channel writer(s)", _writers.Count);
        }
    }

    public async Task BroadcastAsync(
        SignalEventType type,
        Dictionary<string, string> payload,
        string spoolPath,
        CancellationToken cancellationToken = default)
    {
        if (_writers.Count == 0)
        {
            _logger.LogWarning("No channel writers registered, signal {Type} dropped", type);
            return;
        }

        var signal = (type, payload, spoolPath);
        var tasks = new List<Task>(_writers.Count);

        foreach (var writer in _writers)
        {
            tasks.Add(WriteToChannelAsync(writer, signal, type, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private async Task WriteToChannelAsync(
        ChannelWriter<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> writer,
        (SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath) signal,
        SignalEventType type,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(signal, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("Channel closed while broadcasting signal {Type}", type);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast signal {Type} to channel", type);
        }
    }
}
