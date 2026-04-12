using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class WindowedFeatureExtractionTests
{
    [Fact]
    public void SplitSegmentAcrossWindows_ComputesExpectedOverlap()
    {
        var windows = new[]
        {
            new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z")),
            new SlidingWindow(T("2026-03-05T00:00:30Z"), T("2026-03-05T00:01:30Z"))
        };

        var split = SlidingWindowing.SplitSegmentAcrossWindows(
            T("2026-03-05T00:00:20Z"),
            T("2026-03-05T00:01:10Z"),
            windows);

        Assert.Equal(2, split.Count);
        Assert.Equal(40_000, split[0].OverlapMs);
        Assert.Equal(40_000, split[1].OverlapMs);
    }

    [Fact]
    public void SessionLockRatio_IntegratesAcrossWindowBoundaries()
    {
        var aggregator = new SessionFeatureAggregator();
        var window = new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z"));

        var events = new List<FeatureSignal>
        {
            E("2026-03-05T00:00:20Z", SignalEventType.SessionLock),
            E("2026-03-05T00:01:10Z", SignalEventType.SessionUnlock)
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(40.0 / 60.0, result.Features["locked_ratio"], 4);
        Assert.Equal(1.0, result.Features["lock_count"]);
    }

    [Fact]
    public void NetworkWifiRatio_UsesWifiUpStateEvenWhenReasonIsNotWifiPrimary()
    {
        var aggregator = new NetworkFeatureAggregator();
        var window = new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z"));

        var events = new List<FeatureSignal>
        {
            E("2026-03-05T00:00:00Z", SignalEventType.WifiLinkChanged, new() { ["wifiUp"] = "true", ["wifiIdentityReason"] = "connected" }),
            E("2026-03-05T00:00:30Z", SignalEventType.WifiLinkChanged, new() { ["wifiUp"] = "false", ["wifiIdentityReason"] = "not_wifi_primary" })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(0.5, result.Features["primary_wifi_connected_ratio"], 3);
    }

    [Fact]
    public void NoOverlapRule_AwayAndIdleMetricsNotPresentInAppFeatures()
    {
        var aggregator = new AppFeatureAggregator();
        var window = new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z"));

        var events = new List<FeatureSignal>
        {
            E("2026-03-05T00:00:40Z", SignalEventType.AppDwell, new()
            {
                ["appKey"] = "a",
                ["category"] = "IDE",
                ["durationMs"] = "20000",
                ["confidence"] = "high"
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.DoesNotContain("presence_away_ratio", result.Features.Keys);
        Assert.DoesNotContain("idle_ge_60_ratio", result.Features.Keys);
    }

    [Fact]
    public void CrossFeature_GatingExcludesAppSwitchMetricWhenAlwaysLocked()
    {
        var sessionAgg = new SessionFeatureAggregator();
        var appAgg = new AppFeatureAggregator();
        var crossAgg = new CrossFeatureAggregator();
        var window = new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z"));

        var events = new List<FeatureSignal>
        {
            E("2026-03-05T00:00:00Z", SignalEventType.SessionLock),
            E("2026-03-05T00:00:00Z", SignalEventType.DisplayOn, new() { ["displayState"] = "On" }),
            E("2026-03-05T00:00:50Z", SignalEventType.AppDwell, new()
            {
                ["appKey"] = "a",
                ["category"] = "IDE",
                ["durationMs"] = "20000",
                ["confidence"] = "high"
            }),
            E("2026-03-05T00:00:59Z", SignalEventType.AppSwitchRate, new()
            {
                ["windowSec"] = "60",
                ["switches"] = "4"
            })
        };

        var session = sessionAgg.ExtractFeatures(events, window);
        var app = appAgg.ExtractFeatures(events, window);
        var cross = crossAgg.ExtractFeatures(window, session, app);

        Assert.Equal(0.0, cross.Features["active_work_ratio"]);
        Assert.Equal(0.0, cross.Features["app_switches_per_active_min"]);
    }

    [Fact]
    public void SystemResourceAggregator_ComputesNetworkAndLoadFeatures()
    {
        var aggregator = new SystemResourceFeatureAggregator();
        var window = new SlidingWindow(T("2026-03-05T00:00:00Z"), T("2026-03-05T00:01:00Z"));

        var events = new List<FeatureSignal>
        {
            E("2026-03-05T00:00:05Z", SignalEventType.SystemResourceSample, new()
            {
                ["cpu_mean_pct"] = "20",
                ["mem_mean_used_pct"] = "40",
                ["gpu_mean_pct"] = "5",
                ["gpu_mem_used_pct"] = "10",
                ["net_tx_mean_kbps"] = "200",
                ["net_rx_mean_kbps"] = "800"
            }),
            E("2026-03-05T00:00:35Z", SignalEventType.SystemResourceSample, new()
            {
                ["cpu_mean_pct"] = "60",
                ["mem_mean_used_pct"] = "70",
                ["gpu_mean_pct"] = "15",
                ["gpu_mem_used_pct"] = "30",
                ["net_tx_mean_kbps"] = "400",
                ["net_rx_mean_kbps"] = "600"
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(40.0, result.Features["cpu_usage_mean"], 2);
        Assert.Equal(55.0, result.Features["ram_usage_mean"], 2);
        Assert.Equal(700.0, result.Features["net_bytes_total_mean"], 2);
        Assert.Equal(1000.0, result.Features["net_bytes_total_max"], 2);
        Assert.Equal(1.0, result.Features["has_system_data"], 2);
    }

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? payload = null)
    {
        return new FeatureSignal(T(ts), type, payload ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static DateTimeOffset T(string isoUtc) => DateTimeOffset.Parse(isoUtc);
}
