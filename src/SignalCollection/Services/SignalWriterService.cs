using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Contracts;
using EndpointSignalAgent.SignalCollection.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.SignalCollection.Services;

public sealed class SignalWriterService : BackgroundService
{
    private readonly ILogger<SignalWriterService> _logger;
    private readonly ChannelReader<BroadcastSignal> _reader;
    private readonly IEnrollmentStore _enrollmentStore;
    private readonly string _recordingId = Guid.NewGuid().ToString("N");
    private string? _cachedDeviceId;

    public SignalWriterService(
        ILogger<SignalWriterService> logger,
        ISignalWriterChannelReader channelReader,
        IEnrollmentStore enrollmentStore)
    {
        _logger = logger;
        _reader = channelReader.Reader;
        _enrollmentStore = enrollmentStore;
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
                        new SignalEvent(signal.TimestampUtc, signal.Type, signal.Payload),
                        stoppingToken);

                    using var rawWriter = new RawSignalFileCollector(Path.Combine(Path.GetDirectoryName(signal.SpoolPath) ?? "spool", "raw_signals.jsonl"));
                    await rawWriter.WriteAsync(BuildRawRecord(signal), stoppingToken);
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

    private RawCollectorSignalRecord BuildRawRecord(BroadcastSignal signal)
    {
        _cachedDeviceId ??= TryGetDeviceId();
        var provenance = SignalProvenanceCatalog.Resolve(signal.Type);
        var nativeAggregationSec = provenance.Kind == SignalKind.PreAggregated &&
                                   signal.Type == SignalEventType.SystemResourceSample &&
                                   signal.Payload.TryGetValue("window_sec", out var windowSecText) &&
                                   int.TryParse(windowSecText, out var windowSec)
            ? windowSec
            : provenance.NativeAggregationSec;

        return new RawCollectorSignalRecord(
            SchemaVersion: SignalProvenanceCatalog.RawSchemaVersion,
            TimestampUtc: signal.TimestampUtc,
            DeviceId: _cachedDeviceId ?? "unknown-device",
            RecordingId: _recordingId,
            SessionId: null,
            Collector: provenance.Collector,
            SignalType: signal.Type.ToString(),
            SignalKind: ToSnakeCase(provenance.Kind),
            NativeCadenceSec: provenance.NativeCadenceSec,
            NativeAggregationSec: nativeAggregationSec,
            CollectorSchemaVersion: provenance.CollectorSchemaVersion,
            Payload: new Dictionary<string, string>(signal.Payload, StringComparer.Ordinal));
    }

    private string TryGetDeviceId()
    {
        try
        {
            return _enrollmentStore.GetIdAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return Environment.MachineName;
        }
    }

    private static string ToSnakeCase(SignalKind kind) => kind switch
    {
        SignalKind.Event => "event",
        SignalKind.StateSample => "state_sample",
        SignalKind.StateChange => "state_change",
        SignalKind.PreAggregated => "pre_aggregated",
        _ => "event"
    };
}
