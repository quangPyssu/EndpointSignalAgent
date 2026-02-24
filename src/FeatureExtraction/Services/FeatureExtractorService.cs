using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Broadcasting;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.src.FeatureExtraction.SignalAggregator;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Feature Extractor Service - reads signals from its dedicated channel,
/// builds time-windowed feature rows, and stores them.
/// Runs in parallel with SignalWriterService via broadcast pattern.
/// </summary>
public sealed class FeatureExtractorService : BackgroundService
{
    private readonly ILogger<FeatureExtractorService> _logger;
    private readonly ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> _signalReader;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _options;

    private readonly List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> _windowBuffer;
    private DateTimeOffset _windowStart;

    private readonly AppFeatureAggregator _appAggregator;
    private readonly SessionFeatureAggregator _sessionAggregator;
    private readonly NetworkFeatureAggregator _networkAggregator;

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
        _windowBuffer = new List<(DateTimeOffset, SignalEventType, Dictionary<string, string>)>();
        _windowStart = DateTimeOffset.UtcNow;

        _appAggregator = new AppFeatureAggregator();
        _sessionAggregator = new SessionFeatureAggregator();
        _networkAggregator = new NetworkFeatureAggregator();
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
            _logger.LogInformation("FeatureExtractorService: Live extraction is disabled (on-demand mode only)");
            return;
        }

        // Wait for device enrollment
        var deviceId = await _enrollment.GetIdAsync(stoppingToken);

        _logger.LogInformation("FeatureExtractorService started for device {DeviceId} " +
            "(WindowSize: {WindowSize}s, Slide: {Slide}s)",
            deviceId, _options.Value.WindowSizeSeconds, _options.Value.WindowSlideSeconds);

        try
        {
            // Start the window slide timer
            var windowSlideTask = WindowSlideLoopAsync(deviceId, stoppingToken);
            
            // Process incoming signals
            await foreach (var signal in _signalReader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    ProcessSignal(signal, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process signal {Type} for feature extraction", signal.Type);
                }
            }

            await windowSlideTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FeatureExtractorService is shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FeatureExtractorService crashed");
        }

        _logger.LogInformation("FeatureExtractorService stopped");
    }

    private void ProcessSignal(
        (SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath) signal,
        CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;

        // Add to window buffer
        lock (_windowBuffer)
        {
            _windowBuffer.Add((now, signal.Type, signal.Payload));

            // Limit buffer size
            if (_windowBuffer.Count > _options.Value.MaxEventsPerWindow)
            {
                _windowBuffer.RemoveAt(0);
                _logger.LogWarning("Window buffer exceeded max size, dropping oldest event");
            }
        }

        _logger.LogDebug("Buffered signal {Type} for feature extraction (Buffer size: {Size})",
            signal.Type, _windowBuffer.Count);
    }

    private async Task WindowSlideLoopAsync(string deviceId, CancellationToken stoppingToken)
    {
        var slideInterval = TimeSpan.FromSeconds(_options.Value.WindowSlideSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(slideInterval, stoppingToken);

            try
            {
                await ExtractAndStoreFeatures(deviceId, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract and store features");
            }
        }
    }

    private async Task ExtractAndStoreFeatures(string deviceId, CancellationToken stoppingToken)
    {
        var windowEnd = DateTimeOffset.UtcNow;
        var windowSize = TimeSpan.FromSeconds(_options.Value.WindowSizeSeconds);
        var windowStart = windowEnd - windowSize;

        // Keep 1 extra window as history so session/network ratios have carry-in state
        var historyStart = windowStart - windowSize;

        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> eventsContext;

        lock (_windowBuffer)
        {
            eventsContext = _windowBuffer
                .Where(e => e.Timestamp >= historyStart && e.Timestamp <= windowEnd)
                .ToList();

            _windowBuffer.RemoveAll(e => e.Timestamp < historyStart);
        }

        var eventsInWindow = eventsContext
            .Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd)
            .ToList();

        if (eventsInWindow.Count == 0)
        {
            _logger.LogDebug("No events in window {WindowStart} - {WindowEnd}, skipping feature extraction",
                windowStart, windowEnd);
            return;
        }

        // Extract features
        var features = ExtractFeatures(eventsContext, windowStart, windowEnd);


        // Create and store the feature row with new schema
        var featureRow = FeatureRow.CreateNew(
            deviceId: deviceId,
            windowSec: _options.Value.WindowSizeSeconds,
            windowStartTs: windowStart,
            featureVersion: "1.0",
            features: features
        );

        var id = await _featureStore.StoreAsync(featureRow, stoppingToken);

        _logger.LogInformation("Stored feature row {Id} with {FeatureCount} features for window {WindowStart}",
            id, features.Count, windowStart);
    }

    /// <summary>
    /// Extract features from a collection of signal events using specialized aggregators.
    /// Features are organized into three tables: app, session, and network.
    /// </summary>
    private Dictionary<string, object> ExtractFeatures(
    List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> eventsContext,
    DateTimeOffset windowStart,
    DateTimeOffset windowEnd)
    {
        var eventsInWindow = eventsContext
            .Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd)
            .ToList();

        var features = new Dictionary<string, object>();

        // Optional meta (harmless)
        features["event_count"] = eventsInWindow.Count;
        features["unique_event_types"] = eventsInWindow.Select(e => e.Type).Distinct().Count();

        MergeInto(features, _appAggregator.ExtractFeatures(eventsContext, windowStart, windowEnd));
        MergeInto(features, _sessionAggregator.ExtractFeatures(eventsContext, windowStart, windowEnd));
        MergeInto(features, _networkAggregator.ExtractFeatures(eventsContext, windowStart, windowEnd));

        return features;
    }

    private static void MergeInto(Dictionary<string, object> dest, Dictionary<string, object> src)
    {
        foreach (var kv in src)
            dest[kv.Key] = kv.Value; // overwrite on collision (last wins)
    }

    /// <summary>
    /// Extracts features from all signals in the specified jsonl file and stores them.
    /// This method is designed for on-demand batch processing (e.g., via Ctrl+E).
    /// </summary>
    public async Task ExtractFeaturesFromFileAsync(string jsonlPath, CancellationToken ct)
    {
        _logger.LogInformation("Starting on-demand feature extraction from {Path}", jsonlPath);

        if (!File.Exists(jsonlPath))
        {
            _logger.LogWarning("Signal file not found: {Path}", jsonlPath);
            return;
        }

        // Get device ID
        var deviceId = await _enrollment.GetIdAsync(ct);

        // Read all signals from file
        var allSignals = await ReadSignalsFromFileAsync(jsonlPath, ct);

        if (allSignals.Count == 0)
        {
            _logger.LogInformation("No signals found in {Path}", jsonlPath);
            return;
        }

        _logger.LogInformation("Read {Count} signals from file", allSignals.Count);

        // Group signals into windows based on timestamp
        var windowSize = TimeSpan.FromSeconds(_options.Value.WindowSizeSeconds);
        var windowSlide = TimeSpan.FromSeconds(_options.Value.WindowSlideSeconds);

        // Find time range
        var minTime = allSignals.Min(s => s.Timestamp);
        var maxTime = allSignals.Max(s => s.Timestamp);

        _logger.LogInformation("Signal time range: {MinTime} to {MaxTime}", minTime, maxTime);

        var windowCount = 0;
        var currentWindowStart = minTime;

        while (currentWindowStart <= maxTime)
        {
            var windowEnd = currentWindowStart + windowSize;
            var historyStart = currentWindowStart - windowSize;

            // Get events for this window (with history for context)
            var eventsContext = allSignals
                .Where(e => e.Timestamp >= historyStart && e.Timestamp <= windowEnd)
                .ToList();

            var eventsInWindow = eventsContext
                .Where(e => e.Timestamp >= currentWindowStart && e.Timestamp <= windowEnd)
                .ToList();

            if (eventsInWindow.Count > 0)
            {
                // Extract features
                var features = ExtractFeatures(eventsContext, currentWindowStart, windowEnd);

                // Create and store feature row
                var featureRow = FeatureRow.CreateNew(
                    deviceId: deviceId,
                    windowSec: _options.Value.WindowSizeSeconds,
                    windowStartTs: currentWindowStart,
                    featureVersion: "1.0",
                    features: features
                );

                var id = await _featureStore.StoreAsync(featureRow, ct);
                windowCount++;

                _logger.LogDebug("Stored feature row {Id} for window {WindowStart} with {FeatureCount} features",
                    id, currentWindowStart, features.Count);
            }

            // Slide to next window
            currentWindowStart += windowSlide;
        }

        _logger.LogInformation("On-demand extraction complete: created {WindowCount} feature windows from {SignalCount} signals",
            windowCount, allSignals.Count);
    }

    private async Task<List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)>> ReadSignalsFromFileAsync(
        string jsonlPath, 
        CancellationToken ct)
    {
        var signals = new List<(DateTimeOffset, SignalEventType, Dictionary<string, string>)>();

        using var reader = new StreamReader(jsonlPath);
        string? line;
        var lineNumber = 0;

        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var signalEvent = System.Text.Json.JsonSerializer.Deserialize<SignalEvent>(line);
                if (signalEvent != null)
                {
                    signals.Add((signalEvent.TimestampUtc, signalEvent.Type, signalEvent.Payload));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse signal at line {LineNumber}: {Line}", lineNumber, line);
            }
        }

        return signals;
    }

}
