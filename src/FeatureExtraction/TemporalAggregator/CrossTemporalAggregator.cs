using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class CrossTemporalAggregator : ITemporalAggregator
{
    private readonly SessionFeatureAggregator _sessionAgg = new();

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var h300Effective = ctx.WindowEnd.AddSeconds(-300) > ctx.SessionStart
            ? ctx.WindowEnd.AddSeconds(-300)
            : ctx.SessionStart;
        var h300Window    = new SlidingWindow(h300Effective, ctx.WindowEnd);
        var sessionResult = _sessionAgg.ExtractFeatures(ctx.SessionSignals, h300Window);
        var activeList    = CrossFeatureAggregator.BuildActiveWorkIntervals(sessionResult.Intervals);

        var h300Ticks = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.SystemResourceTick)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        double mismatch = 0.0;
        if (h300Ticks.Count > 0)
        {
            int count = 0;
            foreach (var tick in h300Ticks)
            {
                var cpu = PayloadValueReader.GetDouble(tick.Payload, "cpu_pct");
                var mem = PayloadValueReader.GetDouble(tick.Payload, "mem_used_pct");
                if (cpu < 80.0 && mem < 80.0) continue; // not high-resource

                var inActive = activeList.Any(a =>
                    tick.TimestampUtc >= a.StartUtc && tick.TimestampUtc < a.EndUtc);
                if (!inActive) count++;
            }
            mismatch = FeatureMath.SafeDivide(count, h300Ticks.Count);
        }

        yield return new("active_resource_mismatch_h300", mismatch);
    }
}
