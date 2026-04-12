using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Broadcasting;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Live event-time feature extraction service using fixed 60s windows and 30s sliding step.
/// </summary>
public sealed class FeatureExtractorService : BackgroundService
{
    private readonly ILogger<FeatureExtractorService> _logger;
    private readonly ChannelReader<BroadcastSignal> _signalReader;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _options;

    private readonly object _bufferLock = new();
    private readonly List<FeatureSignal> _eventBuffer = new();
    private readonly SemaphoreSlim _emitLock = new(1, 1);

    private DateTimeOffset? _maxEventTsUtc;
    private DateTimeOffset? _nextWindowStartUtc;

    private readonly AppFeatureAggregator _appAggregator = new();
    private readonly SessionFeatureAggregator _sessionAggregator = new();
    private readonly NetworkFeatureAggregator _networkAggregator = new();
    private readonly CrossFeatureAggregator _crossAggregator = new();
    private readonly SystemResourceFeatureAggregator _systemResourceAggregator = new();

    public FeatureExtractorService(
        ILogger<FeatureExtractorService> logger,
        IFeatureExtractorChannelReader channelReader,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> options)
    {
        _logger = logger;
        _signalReader = channelReader.Reader;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("FeatureExtractorService is disabled");
            return;
        }

        if (!_options.Value.EnableLiveExtraction)
        {
            _logger.LogInformation("FeatureExtractorService: live extraction disabled (on-demand mode only)");
            return;
        }

        if (_options.Value.WindowSizeSeconds != FeatureSchema.WindowSec || _options.Value.WindowSlideSeconds != FeatureSchema.StepSec)
        {
            _logger.LogWarning(
                "FeatureExtractorService forcing fixed windowing to {WindowSec}s/{StepSec}s (configured {ConfiguredWindowSec}s/{ConfiguredStepSec}s)",
                FeatureSchema.WindowSec,
                FeatureSchema.StepSec,
                _options.Value.WindowSizeSeconds,
                _options.Value.WindowSlideSeconds);
        }

        var deviceId = await _enrollment.GetIdAsync(stoppingToken);
        _logger.LogInformation(
            "FeatureExtractorService started for device {DeviceId} with fixed event-time windows {WindowSec}s/{StepSec}s",
            deviceId,
            FeatureSchema.WindowSec,
            FeatureSchema.StepSec);

        var timerTask = BackgroundEmitLoopAsync(deviceId, stoppingToken);

        try
        {
            await foreach (var signal in _signalReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    ProcessSignal(signal);
                    await TryEmitDueWindowsAsync(deviceId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Feature extraction signal processing failed for {Type}", signal.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            try
            {
                await timerTask;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }

            await TryEmitDueWindowsAsync(deviceId, stoppingToken, flushAllAvailable: true);
        }

        _logger.LogInformation("FeatureExtractorService stopped");
    }

    private async Task BackgroundEmitLoopAsync(string deviceId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await TryEmitDueWindowsAsync(deviceId, ct);
        }
    }

    private void ProcessSignal(BroadcastSignal signal)
    {
        var featureSignal = new FeatureSignal(
            signal.TimestampUtc,
            signal.Type,
            new Dictionary<string, string>(signal.Payload, StringComparer.Ordinal));

        lock (_bufferLock)
        {
            _eventBuffer.Add(featureSignal);
            _eventBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

            _maxEventTsUtc = SlidingWindowing.Min(_maxEventTsUtc, signal.TimestampUtc) == _maxEventTsUtc
                ? _maxEventTsUtc
                : signal.TimestampUtc;

            if (_maxEventTsUtc is null || signal.TimestampUtc > _maxEventTsUtc.Value)
            {
                _maxEventTsUtc = signal.TimestampUtc;
            }

            if (_nextWindowStartUtc is null)
            {
                var aligned = SlidingWindowing.AlignToStepUtc(signal.TimestampUtc, FeatureSchema.StepSec);
                _nextWindowStartUtc = aligned - TimeSpan.FromSeconds(FeatureSchema.StepSec);
            }

            if (_eventBuffer.Count > Math.Max(1000, _options.Value.MaxEventsPerWindow * 8))
            {
                CompactBufferLocked((_nextWindowStartUtc ?? signal.TimestampUtc) - TimeSpan.FromSeconds(FeatureSchema.WindowSec));
            }
        }
    }

    private async Task TryEmitDueWindowsAsync(string deviceId, CancellationToken ct, bool flushAllAvailable = false)
    {
        if (!await _emitLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            List<(SlidingWindow Window, List<FeatureSignal> Context)> jobs;

            lock (_bufferLock)
            {
                jobs = BuildWindowJobsLocked(flushAllAvailable);
                if (jobs.Count == 0)
                {
                    return;
                }
            }

            foreach (var job in jobs)
            {
                var features = ExtractWindowFeatures(job.Context, job.Window);
                var row = FeatureRow.CreateNew(
                    deviceId: deviceId,
                    windowSec: FeatureSchema.WindowSec,
                    windowStartTs: job.Window.StartUtc,
                    featureVersion: FeatureSchema.FeatureVersion,
                    features: features);

                await _featureStore.StoreAsync(row, ct);
            }

            _logger.LogDebug("Stored {Count} feature windows up to {LastWindowStart}", jobs.Count, jobs[^1].Window.StartUtc);
        }
        finally
        {
            _emitLock.Release();
        }
    }

    private List<(SlidingWindow Window, List<FeatureSignal> Context)> BuildWindowJobsLocked(bool flushAllAvailable)
    {
        var jobs = new List<(SlidingWindow Window, List<FeatureSignal> Context)>();

        if (_nextWindowStartUtc is null || _maxEventTsUtc is null)
        {
            return jobs;
        }

        var watermark = _maxEventTsUtc.Value;
        var maxCompleteWindowStart = SlidingWindowing.AlignToStepUtc(
            watermark - TimeSpan.FromSeconds(FeatureSchema.WindowSec),
            FeatureSchema.StepSec);

        if (_nextWindowStartUtc.Value > maxCompleteWindowStart)
        {
            return jobs;
        }

        var lastStart = flushAllAvailable ? maxCompleteWindowStart : _nextWindowStartUtc.Value;

        foreach (var window in SlidingWindowing.EnumerateWindowStarts(
            _nextWindowStartUtc.Value,
            lastStart,
            FeatureSchema.WindowSec,
            FeatureSchema.StepSec))
        {
            var historyStart = window.StartUtc - TimeSpan.FromSeconds(FeatureSchema.WindowSec + FeatureSchema.StepSec);
            var context = _eventBuffer
                .Where(e => e.TimestampUtc >= historyStart && e.TimestampUtc < window.EndUtc)
                .ToList();

            jobs.Add((window, context));
        }

        _nextWindowStartUtc = lastStart + TimeSpan.FromSeconds(FeatureSchema.StepSec);
        CompactBufferLocked((_nextWindowStartUtc.Value) - TimeSpan.FromSeconds(FeatureSchema.WindowSec + FeatureSchema.StepSec + 10));

        return jobs;
    }

    private void CompactBufferLocked(DateTimeOffset cutoffUtc)
    {
        if (_eventBuffer.Count == 0)
        {
            return;
        }

        var preserveStateTypes = new HashSet<SignalEventType>
        {
            SignalEventType.SessionLock,
            SignalEventType.SessionUnlock,
            SignalEventType.DisplayOn,
            SignalEventType.DisplayOff,
            SignalEventType.DisplayDimmed,
            SignalEventType.ScreenSaverOn,
            SignalEventType.ScreenSaverOff,
            SignalEventType.IdleSample,
            SignalEventType.VpnStateChanged,
            SignalEventType.WifiLinkChanged,
            SignalEventType.WifiSsidChanged,
            SignalEventType.PublicIpBucketChanged
        };

        var latestBeforeCutoffByType = _eventBuffer
            .Where(e => e.TimestampUtc < cutoffUtc && preserveStateTypes.Contains(e.Type))
            .GroupBy(e => e.Type)
            .Select(g => g.OrderByDescending(e => e.TimestampUtc).First())
            .ToList();

        var keep = _eventBuffer
            .Where(e => e.TimestampUtc >= cutoffUtc)
            .Concat(latestBeforeCutoffByType)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        _eventBuffer.Clear();
        _eventBuffer.AddRange(keep);
    }

    internal Dictionary<string, object> ExtractWindowFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var app = _appAggregator.ExtractFeatures(events, window);
        var session = _sessionAggregator.ExtractFeatures(events, window);
        var network = _networkAggregator.ExtractFeatures(events, window);
        var cross = _crossAggregator.ExtractFeatures(window, session, app);
        var system = _systemResourceAggregator.ExtractFeatures(events, window);

        var flat = new Dictionary<string, object>(StringComparer.Ordinal);

        AddOrdered(flat, FeatureSchema.AppColumns, app.Features);
        AddOrdered(flat, FeatureSchema.SessionColumns, session.Features);
        AddOrdered(flat, FeatureSchema.NetworkColumns, network.Features);
        AddOrdered(flat, FeatureSchema.CrossColumns, cross.Features);
        AddOrdered(flat, FeatureSchema.SystemColumns, system.Features);

        return flat;
    }

    private static void AddOrdered(Dictionary<string, object> destination, IEnumerable<string> orderedColumns, IReadOnlyDictionary<string, double> values)
    {
        foreach (var column in orderedColumns)
        {
            destination[column] = values.TryGetValue(column, out var value) ? value : 0.0;
        }
    }

    public async Task ExtractFeaturesFromFileAsync(string jsonlPath, CancellationToken ct)
    {
        _logger.LogInformation("Starting on-demand feature extraction from {Path}", jsonlPath);

        if (!File.Exists(jsonlPath))
        {
            _logger.LogWarning("Signal file not found: {Path}", jsonlPath);
            return;
        }

        var deviceId = await _enrollment.GetIdAsync(ct);
        var allSignals = await ReadSignalsFromFileAsync(jsonlPath, ct);
        if (allSignals.Count == 0)
        {
            _logger.LogInformation("No signals found in {Path}", jsonlPath);
            return;
        }

        var minTs = allSignals.Min(s => s.TimestampUtc);
        var maxTs = allSignals.Max(s => s.TimestampUtc);

        var firstStart = SlidingWindowing.AlignToStepUtc(minTs, FeatureSchema.StepSec) - TimeSpan.FromSeconds(FeatureSchema.StepSec);
        var lastCompleteStart = SlidingWindowing.AlignToStepUtc(maxTs - TimeSpan.FromSeconds(FeatureSchema.WindowSec), FeatureSchema.StepSec);

        var count = 0;
        foreach (var window in SlidingWindowing.EnumerateWindowStarts(firstStart, lastCompleteStart, FeatureSchema.WindowSec, FeatureSchema.StepSec))
        {
            var historyStart = window.StartUtc - TimeSpan.FromSeconds(FeatureSchema.WindowSec + FeatureSchema.StepSec);
            var context = allSignals
                .Where(e => e.TimestampUtc >= historyStart && e.TimestampUtc < window.EndUtc)
                .ToList();

            if (context.Count == 0)
            {
                continue;
            }

            var features = ExtractWindowFeatures(context, window);
            var row = FeatureRow.CreateNew(
                deviceId: deviceId,
                windowSec: FeatureSchema.WindowSec,
                windowStartTs: window.StartUtc,
                featureVersion: FeatureSchema.FeatureVersion,
                features: features);

            await _featureStore.StoreAsync(row, ct);
            count++;
        }

        _logger.LogInformation("On-demand extraction complete: created {Count} windows from {SignalCount} signals", count, allSignals.Count);
    }

    private async Task<List<FeatureSignal>> ReadSignalsFromFileAsync(string jsonlPath, CancellationToken ct)
    {
        var signals = new List<FeatureSignal>();

        using var reader = new StreamReader(jsonlPath);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var ev = System.Text.Json.JsonSerializer.Deserialize<SignalEvent>(line);
                if (ev is null)
                {
                    continue;
                }

                signals.Add(new FeatureSignal(
                    ev.TimestampUtc,
                    ev.Type,
                    new Dictionary<string, string>(ev.Payload, StringComparer.Ordinal)));
            }
            catch
            {
                // Ignore malformed lines.
            }
        }

        signals.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return signals;
    }
}
