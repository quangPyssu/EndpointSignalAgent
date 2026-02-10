using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.src.FeatureExtraction.SignalAggregator;

/// <summary>
/// Aggregates session-related features from signal events based on the session_window_features schema.
/// Handles session state (lock/unlock), display state, screensaver, and idle time tracking.
/// </summary>
public sealed class SessionFeatureAggregator
{
    /// <summary>
    /// Extract session features from events in a time window.
    /// Uses state integration to compute time-based ratios.
    /// </summary>
    public Dictionary<string, object> ExtractFeatures(
        List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> events,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var features = new Dictionary<string, object>();
        var windowDurationMs = (windowEnd - windowStart).TotalMilliseconds;

        // Filter relevant events and sort by timestamp
        var sessionEvents = events
            .Where(e => e.Type == SignalEventType.SessionLock ||
                       e.Type == SignalEventType.SessionUnlock ||
                       e.Type == SignalEventType.DisplayOn ||
                       e.Type == SignalEventType.DisplayOff ||
                       e.Type == SignalEventType.DisplayDimmed ||
                       e.Type == SignalEventType.ScreenSaverOn ||
                       e.Type == SignalEventType.ScreenSaverOff ||
                       e.Type == SignalEventType.IdleSample)
            .OrderBy(e => e.Timestamp)
            .ToList();

        // Data quality indicators
        var inWindow = events.Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd).ToList();

        var hasIdleData = inWindow.Any(e => e.Type == SignalEventType.IdleSample);
        var hasDisplayData = inWindow.Any(e => e.Type == SignalEventType.DisplayOn ||
                                               e.Type == SignalEventType.DisplayOff ||
                                               e.Type == SignalEventType.DisplayDimmed);

        features["has_idle_data"] = hasIdleData ? 1 : 0;
        features["has_display_data"] = hasDisplayData ? 1 : 0;

        features["lock_count"] = inWindow.Count(e => e.Type == SignalEventType.SessionLock);


        // State integration: track state changes over time
        bool isLocked = false;
        bool isDisplayOff = false;
        bool isScreensaverOn = false;
        int currentIdleBucketSec = 0;

        long lockedTimeMs = 0;
        long displayOffTimeMs = 0;
        long screensaverTimeMs = 0;
        long idleBucketWeightedSum = 0;
        long idleBucketTotalTimeMs = 0;
        long idleGe60TimeMs = 0;
        int maxIdleBucketSec = 0;

        // First pass: establish initial state from events before window start
        foreach (var evt in sessionEvents.Where(e => e.Timestamp < windowStart))
        {
            switch (evt.Type)
            {
                case SignalEventType.SessionLock:
                    isLocked = true;
                    break;

                case SignalEventType.SessionUnlock:
                    isLocked = false;
                    break;

                case SignalEventType.DisplayOff:
                case SignalEventType.DisplayDimmed:
                    isDisplayOff = true;
                    break;

                case SignalEventType.DisplayOn:
                    isDisplayOff = false;
                    break;

                case SignalEventType.ScreenSaverOn:
                    isScreensaverOn = true;
                    break;

                case SignalEventType.ScreenSaverOff:
                    isScreensaverOn = false;
                    break;

                case SignalEventType.IdleSample:
                    if (evt.Payload.TryGetValue("idleBucketSec", out var idleBucketStr) &&
                        int.TryParse(idleBucketStr, out var idleBucket))
                    {
                        currentIdleBucketSec = idleBucket;
                        maxIdleBucketSec = Math.Max(maxIdleBucketSec, idleBucket);
                    }
                    break;
            }
        }

        // Second pass: integrate state changes within the window
        DateTimeOffset lastTs = windowStart;

        foreach (var evt in sessionEvents.Where(e => e.Timestamp >= windowStart))
        {
            // Process interval from lastTs to current event timestamp
            var intervalEnd = evt.Timestamp > windowEnd ? windowEnd : evt.Timestamp;
            var intervalMs = (long)(intervalEnd - lastTs).TotalMilliseconds;

            if (intervalMs > 0)
            {
                if (isLocked)
                    lockedTimeMs += intervalMs;
                if (isDisplayOff)
                    displayOffTimeMs += intervalMs;
                if (isScreensaverOn)
                    screensaverTimeMs += intervalMs;

                // Idle bucket weighted sum
                if (hasIdleData)
                {
                    idleBucketWeightedSum += currentIdleBucketSec * intervalMs;
                    idleBucketTotalTimeMs += intervalMs;

                    if (currentIdleBucketSec >= 60)
                        idleGe60TimeMs += intervalMs;
                }
            }

            // Update state based on event type
            switch (evt.Type)
            {
                case SignalEventType.SessionLock:
                    isLocked = true;
                    break;

                case SignalEventType.SessionUnlock:
                    isLocked = false;
                    break;

                case SignalEventType.DisplayOff:
                case SignalEventType.DisplayDimmed:
                    isDisplayOff = true;
                    break;

                case SignalEventType.DisplayOn:
                    isDisplayOff = false;
                    break;

                case SignalEventType.ScreenSaverOn:
                    isScreensaverOn = true;
                    break;

                case SignalEventType.ScreenSaverOff:
                    isScreensaverOn = false;
                    break;

                case SignalEventType.IdleSample:
                    if (evt.Payload.TryGetValue("idleBucketSec", out var idleBucketStr) &&
                        int.TryParse(idleBucketStr, out var idleBucket))
                    {
                        currentIdleBucketSec = idleBucket;
                        maxIdleBucketSec = Math.Max(maxIdleBucketSec, idleBucket);
                    }
                    break;
            }

            lastTs = intervalEnd;

            // If we've reached window end, stop processing
            if (intervalEnd >= windowEnd)
                break;
        }

        // Process final interval from lastTs to windowEnd
        if (lastTs < windowEnd)
        {
            var finalIntervalMs = (long)(windowEnd - lastTs).TotalMilliseconds;

            if (finalIntervalMs > 0)
            {
                if (isLocked)
                    lockedTimeMs += finalIntervalMs;
                if (isDisplayOff)
                    displayOffTimeMs += finalIntervalMs;
                if (isScreensaverOn)
                    screensaverTimeMs += finalIntervalMs;

                if (hasIdleData)
                {
                    idleBucketWeightedSum += currentIdleBucketSec * finalIntervalMs;
                    idleBucketTotalTimeMs += finalIntervalMs;

                    if (currentIdleBucketSec >= 60)
                        idleGe60TimeMs += finalIntervalMs;
                }
            }
        }

        // Compute ratios
        features["locked_ratio"] = windowDurationMs > 0 ? lockedTimeMs / windowDurationMs : 0.0;
        features["display_off_ratio"] = windowDurationMs > 0 ? displayOffTimeMs / windowDurationMs : 0.0;
        features["screensaver_on_ratio"] = windowDurationMs > 0 ? screensaverTimeMs / windowDurationMs : 0.0;

        // Idle statistics
        if (idleBucketTotalTimeMs > 0)
        {
            features["idle_bucket_mean_sec"] = (double)idleBucketWeightedSum / idleBucketTotalTimeMs;
            features["idle_bucket_max_sec"] = maxIdleBucketSec;
            features["idle_ge_60_ratio"] = (double)idleGe60TimeMs / windowDurationMs;
        }
        else
        {
            features["idle_bucket_mean_sec"] = 0.0;
            features["idle_bucket_max_sec"] = 0;
            features["idle_ge_60_ratio"] = 0.0;
        }

        return features;
    }
}
