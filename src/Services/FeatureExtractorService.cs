using System.Threading.Channels;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Identity;
using EndpointSignalAgent.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Services;

/// <summary>
/// Feature Extractor Service - reads signals from the shared channel,
/// builds time-windowed feature rows, and stores them.
/// Runs in parallel with SignalWriterService (both consume from the signal channel).
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

    public FeatureExtractorService(
        ILogger<FeatureExtractorService> logger,
        ChannelReader<(SignalEventType Type, Dictionary<string, string> Payload, string SpoolPath)> signalReader,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> options)
    {
        _logger = logger;
        _signalReader = signalReader;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _options = options;
        _windowBuffer = new List<(DateTimeOffset, SignalEventType, Dictionary<string, string>)>();
        _windowStart = DateTimeOffset.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("FeatureExtractorService is disabled");
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

        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> eventsInWindow;

        lock (_windowBuffer)
        {
            // Get events within the current window
            eventsInWindow = _windowBuffer
                .Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd)
                .ToList();

            // Remove events older than the window
            _windowBuffer.RemoveAll(e => e.Timestamp < windowStart);
        }

        if (eventsInWindow.Count == 0)
        {
            _logger.LogDebug("No events in window {WindowStart} - {WindowEnd}, skipping feature extraction",
                windowStart, windowEnd);
            return;
        }

        _logger.LogInformation("Extracting features from {Count} events in window {WindowStart} - {WindowEnd}",
            eventsInWindow.Count, windowStart, windowEnd);

        // Extract features from the window
        var features = ExtractFeatures(eventsInWindow);

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
    /// Extract features from a collection of signal events.
    /// This is a base implementation - override or extend with domain-specific feature engineering.
    /// </summary>
    private Dictionary<string, object> ExtractFeatures(
        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> events)
    {
        var features = new Dictionary<string, object>();

        // Basic count features
        features["event_count"] = events.Count;
        features["unique_event_types"] = events.Select(e => e.Type).Distinct().Count();

        // Event type distribution
        var typeCounts = events.GroupBy(e => e.Type)
            .ToDictionary(g => $"count_{g.Key}", g => g.Count());
        
        foreach (var (key, value) in typeCounts)
        {
            features[key] = value;
        }

        // Time-based features
        if (events.Count > 1)
        {
            var timestamps = events.Select(e => e.Timestamp).OrderBy(t => t).ToList();
            var intervals = new List<double>();

            for (int i = 1; i < timestamps.Count; i++)
            {
                intervals.Add((timestamps[i] - timestamps[i - 1]).TotalSeconds);
            }

            if (intervals.Any())
            {
                features["avg_interval_seconds"] = intervals.Average();
                features["max_interval_seconds"] = intervals.Max();
                features["min_interval_seconds"] = intervals.Min();
            }
        }

        // Session state features (if applicable)
        var sessionLocks = events.Count(e => e.Type == SignalEventType.SessionLock);
        var sessionUnlocks = events.Count(e => e.Type == SignalEventType.SessionUnlock);
        features["session_lock_count"] = sessionLocks;
        features["session_unlock_count"] = sessionUnlocks;

        // Application usage features
        var appEvents = events.Where(e => e.Type == SignalEventType.ForegroundAppChanged).ToList();
        if (appEvents.Any())
        {
            features["app_switch_count"] = appEvents.Count;
            
            // Unique applications in this window
            var uniqueApps = appEvents
                .Select(e => e.Payload.TryGetValue("app_name", out var name) ? name : "unknown")
                .Distinct()
                .Count();
            features["unique_apps"] = uniqueApps;
        }

        // Network context features
        var networkEvents = events.Where(e => e.Type == SignalEventType.LocalNetworkChanged).ToList();
        features["network_change_count"] = networkEvents.Count;

        // Display state features
        var displayOnCount = events.Count(e => e.Type == SignalEventType.DisplayOn);
        var displayOffCount = events.Count(e => e.Type == SignalEventType.DisplayOff);
        features["display_on_count"] = displayOnCount;
        features["display_off_count"] = displayOffCount;

        // TODO: Add more sophisticated feature extraction logic here
        // Examples: 
        // - Temporal patterns (time of day, day of week)
        // - Sequential patterns
        // - Behavioral anomalies
        // - Context switches
        // - Idle time analysis

        return features;
    }
}
