using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class SessionTemporalAggregator : ITemporalAggregator
{
    private readonly SessionFeatureAggregator _sessionAgg = new();

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        // Build full [SessionStart, WindowEnd) intervals for streak + active ratio
        var fullWindow    = new SlidingWindow(ctx.SessionStart, ctx.WindowEnd);
        var sessionResult = _sessionAgg.ExtractFeatures(ctx.SessionSignals, fullWindow);
        var intervals     = sessionResult.Intervals;
        var activeList    = CrossFeatureAggregator.BuildActiveWorkIntervals(intervals);

        // active_work_ratio_h300: active ms in h300 / h300 valid ms
        var h300Effective = ctx.WindowEnd.AddSeconds(-300) > ctx.SessionStart
            ? ctx.WindowEnd.AddSeconds(-300)
            : ctx.SessionStart;
        var h300ValidMs = (long)(ctx.WindowEnd - h300Effective).TotalMilliseconds;
        var activeH300Ms = 0L;
        foreach (var a in activeList)
            activeH300Ms += SlidingWindowing.OverlapMs(a.StartUtc, a.EndUtc, h300Effective, ctx.WindowEnd);
        yield return Kv("active_work_ratio_h300", FeatureMath.SafeDivide(activeH300Ms, h300ValidMs));

        // active_work_streak_sec: walk intervals in reverse from WindowEnd
        double streakSec = 0.0;
        foreach (var interval in intervals.Reverse())
        {
            var displayOn  = interval.DisplayState == SessionDisplayState.On;
            var presenceOk = !interval.PresenceKnown || !interval.PresenceAway;
            var idleOk     = !interval.IdleKnown || interval.IdleBucketSec < 300;
            var active     = !interval.Locked && displayOn && presenceOk && idleOk;
            if (!active) break;
            streakSec += (interval.EndUtc - interval.StartUtc).TotalSeconds;
        }
        yield return Kv("active_work_streak_sec", streakSec);

        // time_since_unlock_sec: latest SessionUnlock in session, capped at 600
        var latestUnlock = ctx.SessionSignals
            .Where(e => e.Type == SignalEventType.SessionUnlock)
            .Select(e => e.TimestampUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        var timeSinceUnlock = latestUnlock == DateTimeOffset.MinValue
            ? 600.0
            : Math.Min(600.0, (ctx.WindowEnd - latestUnlock).TotalSeconds);
        yield return Kv("time_since_unlock_sec", timeSinceUnlock);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
