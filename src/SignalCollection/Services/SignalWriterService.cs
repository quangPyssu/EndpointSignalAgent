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
    private readonly bool _writeRawSignals;
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
        _writeRawSignals = bool.TryParse(Environment.GetEnvironmentVariable("ESA_WRITE_RAW_SIGNALS"), out var enabled)
            && enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalWriterService started. Raw signal write enabled: {RawSignalsEnabled}", _writeRawSignals);

        try
        {
            await foreach (var signal in _reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    SpoolRotation.RotateIfNeeded(signal.SpoolPath);
                    using var writer = new SpoolFileCollector(signal.SpoolPath);
                    await writer.WriteAsync(
                        new SignalEvent(signal.TimestampUtc, signal.Type, signal.Payload),
                        stoppingToken);

                    if (_writeRawSignals)
                    {
                        var rawPath = Path.Combine(Path.GetDirectoryName(signal.SpoolPath) ?? "spool", "raw_signals.jsonl");
                        SpoolRotation.RotateIfNeeded(rawPath);
                        using var rawWriter = new RawSignalFileCollector(rawPath);
                        await rawWriter.WriteAsync(BuildRawRecord(signal), stoppingToken);
                    }
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
            NativeAggregationSec: provenance.NativeAggregationSec,
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

internal static class SpoolRotation
{
    internal const long DefaultThresholdBytes = 50 * 1024 * 1024; // 50 MB

    internal static void RotateIfNeeded(string spoolPath, long thresholdBytes = DefaultThresholdBytes)
    {
        if (!File.Exists(spoolPath)) return;
        if (new FileInfo(spoolPath).Length < thresholdBytes) return;

        var bakPath = spoolPath + ".bak";
        File.Move(spoolPath, bakPath, overwrite: true);
    }
}
