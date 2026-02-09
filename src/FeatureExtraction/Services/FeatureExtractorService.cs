using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Broadcasting;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
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
        var features = ExtractFeatures(eventsInWindow, windowStart, windowEnd);

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
        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> events,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var features = new Dictionary<string, object>();

        // Basic count features
        features["event_count"] = events.Count;
        features["unique_event_types"] = events.Select(e => e.Type).Distinct().Count();

        // Extract app features
        var appFeatures = _appAggregator.ExtractFeatures(events, windowStart, windowEnd);
        foreach (var (key, value) in appFeatures)
        {
            features[key] = value;
        }

        // Extract session features
        var sessionFeatures = _sessionAggregator.ExtractFeatures(events, windowStart, windowEnd);
        foreach (var (key, value) in sessionFeatures)
        {
            features[key] = value;
        }

        // Extract network features
        var networkFeatures = _networkAggregator.ExtractFeatures(events, windowStart, windowEnd);
        foreach (var (key, value) in networkFeatures)
        {
            features[key] = value;
        }

        return features;
    }
}
