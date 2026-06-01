using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class TemporalMetadataAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        yield return Kv("session_age_sec", (ctx.WindowEnd - ctx.SessionStart).TotalSeconds);

        var coverH300 = Math.Min(1.0, FeatureMath.SafeDivide(ctx.H300ValidSec, 300.0));
        yield return Kv("history_coverage_ratio_h300", coverH300);

        yield return Kv("history_coverage_ratio_h600",
            Math.Min(1.0, FeatureMath.SafeDivide(ctx.H600ValidSec, 600.0)));

        yield return Kv("is_temporal_warmup_window", coverH300 < 1.0 ? 1.0 : 0.0);

        yield return Kv("raw_event_count_h300", ctx.H300Signals.Count);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
