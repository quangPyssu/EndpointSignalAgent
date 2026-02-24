using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.src.FeatureExtraction.SignalAggregator;

/// <summary>
/// Aggregates app-related features from signal events based on the app_window_features schema.
/// Handles ForegroundAppChanged and AppDwell events to compute app usage patterns.
/// </summary>
public sealed class AppFeatureAggregator
{
    /// <summary>
    /// Extract app features from events in a time window.
    /// </summary>
    public Dictionary<string, object> ExtractFeatures(
    List<(DateTimeOffset Timestamp, SignalEventType Type, Dictionary<string, string> Payload)> events,
    DateTimeOffset windowStart,
    DateTimeOffset windowEnd)
    {
        var features = new Dictionary<string, object>();
        var windowDurationMs = (windowEnd - windowStart).TotalMilliseconds;

        // IMPORTANT: restrict counts to window
        var inWindow = events.Where(e => e.Timestamp >= windowStart && e.Timestamp <= windowEnd).ToList();

        var appSwitchEvents = inWindow.Where(e => e.Type == SignalEventType.ForegroundAppChanged).ToList();
        var appDwellEvents = events.Where(e => e.Type == SignalEventType.AppDwell).ToList();

        features["app_switch_count"] = appSwitchEvents.Count;

        var appDwellSegments = new List<(string AppKey, string Category, long OverlapMs)>();

        foreach (var dwellEvent in appDwellEvents)
        {
            if (!dwellEvent.Payload.TryGetValue("durationMs", out var durationStr) ||
                !long.TryParse(durationStr, out var durationMs))
                continue;

            var appKey = dwellEvent.Payload.TryGetValue("appKey", out var key) ? key : "unknown";
            var rawCategory = dwellEvent.Payload.TryGetValue("category", out var cat) ? cat : "Other";
            var category = NormalizeCategory(rawCategory);

            var segEnd = dwellEvent.Timestamp;
            var segStart = segEnd.AddMilliseconds(-durationMs);

            var overlapStart = segStart > windowStart ? segStart : windowStart;
            var overlapEnd = segEnd < windowEnd ? segEnd : windowEnd;
            var overlapMs = (long)Math.Max(0, (overlapEnd - overlapStart).TotalMilliseconds);

            if (overlapMs > 0)
                appDwellSegments.Add((appKey, category, overlapMs));
        }

        features["has_app_data"] = (appSwitchEvents.Any() || appDwellSegments.Any()) ? 1 : 0;

        // app_unique_count: if no dwell, fallback to switch distinct appKey (helps stability)
        if (appDwellSegments.Any())
        {
            features["app_unique_count"] = appDwellSegments.Select(s => s.AppKey).Distinct().Count();
        }
        else
        {
            features["app_unique_count"] = appSwitchEvents
                .Select(e => e.Payload.TryGetValue("appKey", out var k) ? k : "unknown")
                .Distinct()
                .Count();
        }

        // Dwell statistics
        if (appDwellSegments.Any())
        {
            var dwellValues = appDwellSegments.Select(s => (double)s.OverlapMs).ToList();
            features["app_dwell_mean_ms"] = dwellValues.Average();
            features["app_dwell_std_ms"] = CalculateStandardDeviation(dwellValues);
            features["app_dwell_max_ms"] = dwellValues.Max();

            // Total dwell time
            var totalDwellMs = dwellValues.Sum();

            // app_top1_share: max_app_dwell_ms / max(total_dwell_ms, 1)
            var appDwellByKey = appDwellSegments.GroupBy(s => s.AppKey)
                .Select(g => g.Sum(s => s.OverlapMs))
                .ToList();

            if (appDwellByKey.Any())
            {
                var maxAppDwell = appDwellByKey.Max();
                features["app_top1_share"] = maxAppDwell / Math.Max(totalDwellMs, 1.0);
            }
            else
            {
                features["app_top1_share"] = 0.0;
            }
        }
        else
        {
            features["app_dwell_mean_ms"] = 0.0;
            features["app_dwell_std_ms"] = 0.0;
            features["app_dwell_max_ms"] = 0.0;
            features["app_top1_share"] = 0.0;
        }

        // Category ratio columns: cat_browser_ratio, cat_ide_ratio, cat_comms_ratio, cat_other_ratio
        var categoryDwell = appDwellSegments
        .GroupBy(s => s.Category)
        .ToDictionary(g => g.Key, g => g.Sum(s => s.OverlapMs));

        double GetCatMs(string k) => categoryDwell.TryGetValue(k, out var v) ? v : 0;

        var browserMs = GetCatMs("browser");
        var ideMs = GetCatMs("ide");
        var commsMs = GetCatMs("comms");

        var otherMs = categoryDwell
            .Where(kv => kv.Key != "browser" && kv.Key != "ide" && kv.Key != "comms")
            .Sum(kv => kv.Value);

        var totalCategoryMs = browserMs + ideMs + commsMs + otherMs;

        features["cat_browser_ratio"] = totalCategoryMs > 0 ? browserMs / totalCategoryMs : 0.0;
        features["cat_ide_ratio"] = totalCategoryMs > 0 ? ideMs / totalCategoryMs : 0.0;
        features["cat_comms_ratio"] = totalCategoryMs > 0 ? commsMs / totalCategoryMs : 0.0;
        features["cat_other_ratio"] = totalCategoryMs > 0 ? otherMs / totalCategoryMs : 0.0;

        return features;
    }

    private static string NormalizeCategory(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "browser" => "browser",
            "ide" => "ide",
            "comms" => "comms",
            _ => "other"
        };
    }

    private static double CalculateStandardDeviation(List<double> values)
    {
        if (values.Count <= 1)
            return 0.0;

        var mean = values.Average();
        var sumOfSquares = values.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumOfSquares / values.Count);
    }
}
