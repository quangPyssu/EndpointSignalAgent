using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.SignalCollection.Services;

public sealed class SignalWriterService : BackgroundService
{
    private readonly ILogger<SignalWriterService> _logger;
    private readonly ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> _reader;

    public SignalWriterService(
        ILogger<SignalWriterService> logger,
        ISignalWriterChannelReader channelReader)
    {
        _logger = logger;
        _reader = channelReader.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalWriterService started.");

        try
        {
            await foreach (var signal in _reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var writer = new SpoolFileCollector(signal.SpoolPath);
                    await writer.WriteAsync(
                        new SignalEvent(DateTimeOffset.UtcNow, signal.Type, signal.Payload),
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to write signal {Type} to {SpoolPath}", 
                        signal.Type, signal.SpoolPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SignalWriterService is shutting down.");
        }

        _logger.LogInformation("SignalWriterService stopped.");
    }
}
