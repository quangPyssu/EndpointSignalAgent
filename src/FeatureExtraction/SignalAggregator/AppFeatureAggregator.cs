using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Focus/task-switching features sourced only from ApplicationUsageCollector signals.
/// </summary>
internal sealed class AppFeatureAggregator
{
    public AppFeatureResult ExtractFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var features = FeatureSchema.AppColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var appEvents = events
            .Where(e => e.Type == SignalEventType.AppDwell || e.Type == SignalEventType.ForegroundAppChanged || e.Type == SignalEventType.AppSwitchRate)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        var windowEvents = appEvents
            .Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc)
            .ToList();

        var overlapSegments = BuildOverlapSegments(appEvents, window);
        var totalDwellMs = overlapSegments.Sum(x => (double)x.OverlapMs);

        features["has_app_data"] = (overlapSegments.Count > 0 || windowEvents.Count > 0) ? 1.0 : 0.0;
        features["app_unique_count"] = overlapSegments.Select(x => x.AppKey).Distinct(StringComparer.Ordinal).Count();

        var dwellValues = overlapSegments.Select(x => (double)x.OverlapMs).ToList();
        if (dwellValues.Count > 0)
        {
            features["app_dwell_mean_ms"] = dwellValues.Average();
            features["app_dwell_std_ms"] = FeatureMath.StdDev(dwellValues);
            features["app_dwell_max_ms"] = dwellValues.Max();

            var topDwellMs = overlapSegments
                .GroupBy(x => x.AppKey, StringComparer.Ordinal)
                .Select(g => g.Sum(x => x.OverlapMs))
                .DefaultIfEmpty(0L)
                .Max();
            features["app_top1_share"] = FeatureMath.SafeDivide(topDwellMs, Math.Max(totalDwellMs, 1.0));
        }

        var categoryMs = FeatureSchema.CategoryToColumn.Keys.ToDictionary(k => k, _ => 0.0, StringComparer.Ordinal);
        foreach (var segment in overlapSegments)
        {
            categoryMs[segment.Category] += segment.OverlapMs;
        }

        foreach (var kv in FeatureSchema.CategoryToColumn)
        {
            features[kv.Value] = FeatureMath.SafeDivide(categoryMs[kv.Key], Math.Max(window.DurationMs, 1L));
        }

        var confidenceHighMs = overlapSegments.Where(x => x.ConfidenceHigh).Sum(x => (double)x.OverlapMs);
        features["app_confidence_high_ratio"] = FeatureMath.SafeDivide(confidenceHighMs, Math.Max(totalDwellMs, 1.0));

        features["no_foreground_end_count"] = windowEvents.Count(e =>
            e.Type == SignalEventType.AppDwell &&
            string.Equals(PayloadValueReader.GetString(e.Payload, "reason", PayloadValueReader.GetString(e.Payload, "dwellReason")), "no_foreground", StringComparison.OrdinalIgnoreCase));

        var fgChanges = windowEvents.Where(e => e.Type == SignalEventType.ForegroundAppChanged).ToList();
        var hookChanges = fgChanges.Count(e =>
            string.Equals(PayloadValueReader.GetString(e.Payload, "collectorMode"), "hook", StringComparison.OrdinalIgnoreCase));
        features["collector_mode_hook_ratio"] = FeatureMath.SafeDivide(hookChanges, Math.Max(fgChanges.Count, 1));

        var switchRateInWindow = windowEvents
            .Where(e => e.Type == SignalEventType.AppSwitchRate)
            .OrderByDescending(e => e.TimestampUtc)
            .FirstOrDefault();

        if (switchRateInWindow.Payload is not null &&
            PayloadValueReader.TryGetInt(switchRateInWindow.Payload, "switches", out var switchCountFromRate))
        {
            features["app_switch_count"] = Math.Max(0, switchCountFromRate);
        }
        else
        {
            features["app_switch_count"] = fgChanges.Count;
        }

        return new AppFeatureResult(features, features["app_switch_count"], overlapSegments);
    }

    private static List<AppOverlapSegment> BuildOverlapSegments(IReadOnlyList<FeatureSignal> appEvents, SlidingWindow window)
    {
        var overlaps = new List<AppOverlapSegment>();
        var targetWindows = new[] { window };

        foreach (var evt in appEvents)
        {
            if (evt.Type != SignalEventType.AppDwell)
            {
                continue;
            }

            if (!PayloadValueReader.TryGetLong(evt.Payload, "durationMs", out var durationMs) || durationMs <= 0)
            {
                continue;
            }

            var segEnd = evt.TimestampUtc;
            var segStart = segEnd - TimeSpan.FromMilliseconds(durationMs);
            var appKey = PayloadValueReader.GetString(evt.Payload, "appKey", "unknown");
            var category = NormalizeCategory(PayloadValueReader.GetString(evt.Payload, "category", "Other"));
            var confidenceHigh = string.Equals(PayloadValueReader.GetString(evt.Payload, "confidence", "low"), "high", StringComparison.OrdinalIgnoreCase);

            var split = SlidingWindowing.SplitSegmentAcrossWindows(segStart, segEnd, targetWindows);
            foreach (var item in split)
            {
                overlaps.Add(new AppOverlapSegment(appKey, category, item.OverlapStartUtc, item.OverlapEndUtc, item.OverlapMs, confidenceHigh));
            }
        }

        return overlaps;
    }

    private static string NormalizeCategory(string category)
    {
        var compact = new string(category
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (FeatureSchema.CategoryToColumn.ContainsKey(compact))
        {
            return compact;
        }

        return "other";
    }
}

