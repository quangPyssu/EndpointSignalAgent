using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed class NetworkTemporalAggregator : ITemporalAggregator
{
    private static readonly HashSet<SignalEventType> NetworkTypes = new()
    {
        SignalEventType.VpnStateChanged,
        SignalEventType.WifiLinkChanged,
        SignalEventType.WifiSsidChanged,
        SignalEventType.LocalNetworkChanged,
        SignalEventType.PublicIpBucketChanged
    };

    public IEnumerable<KeyValuePair<string, double>> Aggregate(TemporalWindowContext ctx)
    {
        yield return Kv("network_change_count_h300",
            ctx.H300Signals.Count(IsNonInitial));

        var latestChange = ctx.SessionSignals
            .Where(IsNonInitial)
            .Select(e => e.TimestampUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        yield return Kv("time_since_network_change_sec",
            latestChange == DateTimeOffset.MinValue
                ? 600.0
                : Math.Min(600.0, (ctx.WindowEnd - latestChange).TotalSeconds));

        var h600Changes = ctx.H600Signals.Where(IsNonInitial).OrderBy(e => e.TimestampUtc).ToList();
        if (h600Changes.Count == 0)
        {
            yield return Kv("network_context_stability_ratio_h600", 1.0);
        }
        else
        {
            var stableSec = (ctx.WindowEnd - h600Changes.Last().TimestampUtc).TotalSeconds;
            yield return Kv("network_context_stability_ratio_h600",
                FeatureMath.SafeDivide(stableSec, ctx.H600ValidSec));
        }
    }

    private static bool IsNonInitial(FeatureSignal e) =>
        NetworkTypes.Contains(e.Type) &&
        !PayloadValueReader.IsTruthy(PayloadValueReader.GetString(e.Payload, "initial"));

    private static KeyValuePair<string, double> Kv(string k, double v) => new(k, v);
}
