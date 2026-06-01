using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class AppTemporalAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var switches = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.ForegroundAppChanged)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        // Build committed category-transition pairs
        var transitionPairs = new List<string>();
        string? prevCat = null;
        foreach (var ev in switches)
        {
            var cat = PayloadValueReader.GetString(ev.Payload, "category", "other");
            if (prevCat is not null && cat != prevCat)
                transitionPairs.Add($"{prevCat}→{cat}");
            prevCat = cat;
        }

        yield return Kv("category_transition_count_h300", transitionPairs.Count);

        double entropy = 0.0;
        if (transitionPairs.Count > 0)
        {
            var total = (double)transitionPairs.Count;
            var shares = transitionPairs
                .GroupBy(p => p)
                .Select(g => g.Count() / total);
            entropy = FeatureMath.EntropyFromShares(shares);
        }
        yield return Kv("category_transition_entropy_h300", entropy);

        double switchStd = 0.0;
        if (switches.Count >= 2)
        {
            var intervals = switches
                .Zip(switches.Skip(1), (a, b) => (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds)
                .ToList();
            switchStd = FeatureMath.StdDev(intervals);
        }
        yield return Kv("app_switch_interval_std_ms_h300", switchStd);

        var dwells = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.AppDwell)
            .Select(e => PayloadValueReader.GetDouble(e.Payload, "durationMs"))
            .Where(ms => ms > 0)
            .ToList();

        double cv = 0.0;
        if (dwells.Count >= 2)
        {
            var mean = dwells.Average();
            if (mean > 0)
                cv = FeatureMath.StdDev(dwells) / mean;
        }
        yield return Kv("app_dwell_cv_h300", cv);
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
