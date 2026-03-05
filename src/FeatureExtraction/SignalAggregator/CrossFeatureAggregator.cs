namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Small gated cross-layer features; uses session-derived active-work intervals to gate app metrics.
/// </summary>
internal sealed class CrossFeatureAggregator
{
    public CrossFeatureResult ExtractFeatures(
        SlidingWindow window,
        SessionFeatureResult session,
        AppFeatureResult app)
    {
        var features = FeatureSchema.CrossColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var activeIntervals = BuildActiveWorkIntervals(session.Intervals);
        var activeMs = activeIntervals.Sum(i => Math.Max(0L, (long)(i.EndUtc - i.StartUtc).TotalMilliseconds));
        var windowMs = Math.Max(1L, window.DurationMs);

        features["active_work_ratio"] = FeatureMath.SafeDivide(activeMs, windowMs);

        if (activeMs <= 0)
        {
            features["app_switches_per_active_min"] = 0.0;
            features["category_entropy_active"] = 0.0;
            return new CrossFeatureResult(features);
        }

        var activeMinutes = activeMs / 60000.0;
        features["app_switches_per_active_min"] = app.AppSwitchCount / activeMinutes;

        var categoryMs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var segment in app.OverlapSegments)
        {
            foreach (var active in activeIntervals)
            {
                var overlap = SlidingWindowing.OverlapMs(segment.StartUtc, segment.EndUtc, active.StartUtc, active.EndUtc);
                if (overlap <= 0)
                {
                    continue;
                }

                if (!categoryMs.ContainsKey(segment.Category))
                {
                    categoryMs[segment.Category] = 0;
                }

                categoryMs[segment.Category] += overlap;
            }
        }

        var totalCategoryMs = categoryMs.Values.Sum();
        if (totalCategoryMs > 0)
        {
            var shares = categoryMs.Values.Select(v => v / totalCategoryMs);
            features["category_entropy_active"] = FeatureMath.EntropyFromShares(shares);
        }

        return new CrossFeatureResult(features);
    }

    internal static List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> BuildActiveWorkIntervals(IReadOnlyList<SessionInterval> intervals)
    {
        var result = new List<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();

        foreach (var interval in intervals)
        {
            if (interval.EndUtc <= interval.StartUtc)
            {
                continue;
            }

            var displayOn = interval.DisplayState == SessionDisplayState.On;
            var presenceOk = !interval.PresenceKnown || !interval.PresenceAway;
            var idleOk = !interval.IdleKnown || interval.IdleBucketSec < 300;
            var active = !interval.Locked && displayOn && presenceOk && idleOk;

            if (active)
            {
                result.Add((interval.StartUtc, interval.EndUtc));
            }
        }

        return result;
    }
}
