using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class NetworkTemporalAggregatorTests
{
    [Fact]
    public void NetworkChangeCountH300_ExcludesInitialSnapshots()
    {
        var (sessionStart, windowEnd) = Session(700);
        var h300Start = windowEnd.AddSeconds(-300);

        var signals = new[]
        {
            E(h300Start.AddSeconds(10), SignalEventType.VpnStateChanged,
                new() { ["initial"] = "true" }),           // excluded
            E(h300Start.AddSeconds(20), SignalEventType.VpnStateChanged,
                new() { ["vpnOn"] = "true" }),             // counted
            E(h300Start.AddSeconds(30), SignalEventType.WifiLinkChanged,
                new() { ["wifiUp"] = "false" }),           // counted
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(2.0, features["network_change_count_h300"]);
    }

    [Fact]
    public void NetworkChangeCountH300_IsZero_WhenOnlyInitialSignals()
    {
        var (sessionStart, windowEnd) = Session(700);
        var h300Start = windowEnd.AddSeconds(-300);

        var signals = new[]
        {
            E(h300Start.AddSeconds(5), SignalEventType.VpnStateChanged, new() { ["initial"] = "true" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(0.0, features["network_change_count_h300"]);
    }

    [Fact]
    public void TimeSinceNetworkChange_IsCappedAt600()
    {
        var (sessionStart, windowEnd) = Session(1000);
        // Last change 800 s before window_end → capped at 600
        var signals = new[]
        {
            E(windowEnd.AddSeconds(-800), SignalEventType.VpnStateChanged, new() { ["vpnOn"] = "true" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_network_change_sec"]);
    }

    [Fact]
    public void TimeSinceNetworkChange_Is600_WhenNoChangeEverObserved()
    {
        var (sessionStart, windowEnd) = Session(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());
        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_network_change_sec"]);
    }

    [Fact]
    public void NetworkContextStabilityRatioH600_IsOne_WhenNoChangesInH600()
    {
        var (sessionStart, windowEnd) = Session(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());
        var features = Run(ctx);

        Assert.Equal(1.0, features["network_context_stability_ratio_h600"], precision: 3);
    }

    [Fact]
    public void NetworkContextStabilityRatioH600_EqualsStableTimeFraction()
    {
        // Session 700 s old. h600 valid = 600 s. Change at WindowEnd-200 s.
        // Stable since: 200 s → ratio = 200/600 ≈ 0.333
        var (sessionStart, windowEnd) = Session(700);
        var signals = new[]
        {
            E(windowEnd.AddSeconds(-200), SignalEventType.LocalNetworkChanged),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);

        Assert.Equal(200.0 / 600.0, features["network_context_stability_ratio_h600"], precision: 3);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx) =>
        new NetworkTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static (DateTimeOffset sessionStart, DateTimeOffset windowEnd) Session(int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return (s, s.AddSeconds(ageSec));
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(DateTimeOffset ts, SignalEventType type,
        Dictionary<string, string>? p = null) =>
        new(ts, type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
