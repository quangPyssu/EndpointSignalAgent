using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class SystemTemporalAggregator : ITemporalAggregator
{
    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        var ticks = ctx.H300Signals
            .Where(e => e.Type == SignalEventType.SystemResourceTick)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        // cpu_high_persistence_h300
        double cpuHigh = 0.0;
        if (ticks.Count > 0)
        {
            var cpu = ticks.Select(e => PayloadValueReader.GetDouble(e.Payload, "cpu_pct")).ToList();
            cpuHigh = FeatureMath.SafeDivide(cpu.Count(v => v >= 80.0), cpu.Count);
        }
        yield return Kv("cpu_high_persistence_h300", cpuHigh);

        // net_throughput_trend_h300 — OLS slope over sample index
        double trend = 0.0;
        if (ticks.Count >= 2)
        {
            var x = Enumerable.Range(0, ticks.Count).Select(i => (double)i).ToArray();
            var y = ticks.Select(e =>
                PayloadValueReader.GetDouble(e.Payload, "net_rx_kbps") +
                PayloadValueReader.GetDouble(e.Payload, "net_tx_kbps")).ToArray();
            trend = OlsSlope(x, y);
        }
        yield return Kv("net_throughput_trend_h300", trend);
    }

    private static double OlsSlope(double[] x, double[] y)
    {
        var meanX = x.Average();
        var meanY = y.Average();
        var num = x.Zip(y, (xi, yi) => (xi - meanX) * (yi - meanY)).Sum();
        var den = x.Sum(xi => (xi - meanX) * (xi - meanX));
        return den < 1e-9 ? 0.0 : num / den;
    }

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
