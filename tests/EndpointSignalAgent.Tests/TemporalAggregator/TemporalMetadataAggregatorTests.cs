using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalMetadataAggregatorTests
{
    private readonly TemporalMetadataAggregator _agg = new();

    [Fact]
    public void SessionAgeSec_IsWindowEndMinusSessionStart()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(450);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(450.0, features["session_age_sec"], precision: 1);
    }

    [Fact]
    public void HistoryCoverageRatio_ClipsAtOne_WhenSessionOlderThanHorizon()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["history_coverage_ratio_h300"], precision: 3);
        Assert.Equal(1.0, features["history_coverage_ratio_h600"], precision: 3);
        Assert.Equal(0.0, features["is_temporal_warmup_window"]);
    }

    [Fact]
    public void IsTemporalWarmupWindow_IsOne_WhenCoverageLessThanFull()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(200); // < 300 s
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["is_temporal_warmup_window"]);
    }

    [Fact]
    public void RawEventCountH300_CountsOnlyH300Signals()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");
        // h300 = [10:55:00, 11:00:00)
        var signals = new[]
        {
            E("2026-06-01T10:56:00Z", SignalEventType.ForegroundAppChanged), // in h300
            E("2026-06-01T10:52:00Z", SignalEventType.ForegroundAppChanged), // outside h300
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = ToDictionary(_agg.Aggregate(ctx));

        Assert.Equal(1.0, features["raw_event_count_h300"]);
    }

    private static Dictionary<string, double> ToDictionary(IEnumerable<KeyValuePair<string, double>> src)
        => src.ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? p = null) =>
        new(T(ts), type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
