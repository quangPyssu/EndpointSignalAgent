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
            E("2026-03-05T00:00:05Z", SignalEventType.SystemResourceTick, new()
            {
                ["cpu_available"] = "true",
                ["cpu_pct"] = "20",
                ["mem_available"] = "true",
                ["mem_used_pct"] = "40",
                ["gpu_available"] = "true",
                ["gpu_pct"] = "5",
                ["gpu_mem_used_pct"] = "10",
                ["net_tx_kbps"] = "200",
                ["net_rx_kbps"] = "800"
            }),
            E("2026-03-05T00:00:35Z", SignalEventType.SystemResourceTick, new()
            {
                ["cpu_available"] = "true",
                ["cpu_pct"] = "60",
                ["mem_available"] = "true",
                ["mem_used_pct"] = "70",
                ["gpu_available"] = "true",
                ["gpu_pct"] = "15",
                ["gpu_mem_used_pct"] = "30",
                ["net_tx_kbps"] = "400",
                ["net_rx_kbps"] = "600"
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(40.0, result.Features["cpu_usage_mean"], 2);
        Assert.Equal(55.0, result.Features["ram_usage_mean"], 2);
        Assert.Equal(700.0, result.Features["net_bytes_total_mean"], 2);
        Assert.Equal(1000.0, result.Features["net_bytes_total_max"], 2);
        Assert.Equal(1.0, result.Features["has_system_data"], 2);
    }

    [Fact]
    public void PayloadValueReader_TryGetDateTimeOffset_ParsesRoundTripFormat()
    {
        var expected = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
        var payload = new Dictionary<string, string>
        {
            ["ts"] = expected.ToString("O")
        };

        var found = PayloadValueReader.TryGetDateTimeOffset(payload, "ts", out var actual);

        Assert.True(found);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PayloadValueReader_TryGetDateTimeOffset_ReturnsFalseForMissingKey()
    {
        var payload = new Dictionary<string, string>();
        Assert.False(PayloadValueReader.TryGetDateTimeOffset(payload, "ts", out _));
    }

    [Fact]
    public void AppFeatures_SustainedFocus_ProducesNonZeroTop1Share()
    {
        // User in "code" for 2 minutes with no app switch — only heartbeat, no AppDwell.
        var aggregator = new AppFeatureAggregator();
        var windowStart = DateTimeOffset.Parse("2026-06-11T10:02:00Z");
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(30));
        var dwellStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");

        var events = new List<FeatureSignal>
        {
            new(windowStart + TimeSpan.FromSeconds(25), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
            {
                ["appKey"] = "abc123",
                ["category"] = "IDE",
                ["confidence"] = "high",
                ["dwellStartUtc"] = dwellStart.ToString("O")
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(1.0, result.Features["app_top1_share"], 3);
        Assert.Equal(1.0, result.Features["has_app_data"], 3);
        Assert.True(result.Features["cat_ide_ratio"] > 0.0, "cat_ide_ratio should be > 0 during sustained IDE focus");
        Assert.Equal(1.0, result.Features["app_confidence_high_ratio"], 3);
    }

    [Fact]
    public void AppFeatures_OpenDwell_ClipsToWindowEnd()
    {
        // Dwell started 20s into window — only 40s of the 60s window is covered.
        var aggregator = new AppFeatureAggregator();
        var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));
        var dwellStart = windowStart + TimeSpan.FromSeconds(20);

        var events = new List<FeatureSignal>
        {
            new(windowStart + TimeSpan.FromSeconds(50), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
            {
                ["appKey"] = "abc123",
                ["category"] = "Browser",
                ["confidence"] = "high",
                ["dwellStartUtc"] = dwellStart.ToString("O")
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        Assert.Equal(40.0 / 60.0, result.Features["cat_browser_ratio"], 3);
        Assert.Equal(1.0, result.Features["app_top1_share"], 3);
    }

    [Fact]
    public void AppFeatures_NoDoubleCount_WhenRealDwellAndHeartbeatBothPresent()
    {
        // Real AppDwell closes the dwell — heartbeat must NOT add a synthetic segment.
        var aggregator = new AppFeatureAggregator();
        var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));
        var dwellStart = windowStart - TimeSpan.FromSeconds(10);
        var dwellEnd = windowStart + TimeSpan.FromSeconds(40);

        var events = new List<FeatureSignal>
        {
            // Real AppDwell: 50s total, 40s overlap with window
            new(dwellEnd, SignalEventType.AppDwell, new Dictionary<string, string>
            {
                ["appKey"] = "abc123",
                ["category"] = "IDE",
                ["durationMs"] = "50000",
                ["confidence"] = "high",
                ["reason"] = "switch"
            }),
            // Heartbeat present in context (emitted before the switch closed the dwell)
            new(dwellEnd - TimeSpan.FromSeconds(5), SignalEventType.AppFocusHeartbeat, new Dictionary<string, string>
            {
                ["appKey"] = "abc123",
                ["category"] = "IDE",
                ["confidence"] = "high",
                ["dwellStartUtc"] = dwellStart.ToString("O")
            })
        };

        var result = aggregator.ExtractFeatures(events, window);

        // Exactly 40s / 60s from real AppDwell — no duplication
        Assert.Equal(40.0 / 60.0, result.Features["cat_ide_ratio"], 3);
    }

    [Fact]
    public void AppFeatures_NoHeartbeat_NoSyntheticSegment_BackwardCompat()
    {
        // No events at all — no regression from old behavior.
        var aggregator = new AppFeatureAggregator();
        var windowStart = DateTimeOffset.Parse("2026-06-11T10:00:00Z");
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

        var result = aggregator.ExtractFeatures(new List<FeatureSignal>(), window);

        Assert.Equal(0.0, result.Features["app_top1_share"]);
        Assert.Equal(0.0, result.Features["has_app_data"]);
    }

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? payload = null)
    {
        return new FeatureSignal(T(ts), type, payload ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static DateTimeOffset T(string isoUtc) => DateTimeOffset.Parse(isoUtc);
}
