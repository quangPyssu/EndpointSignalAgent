using EndpointSignalAgent.FeatureExtraction.Services;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class AppDwellReplayPreprocessorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-06-11T10:00:00Z");

    private static FeatureSignal ForegroundChanged(DateTimeOffset ts, string appKey, string category = "IDE", string confidence = "high") =>
        new(ts, SignalEventType.ForegroundAppChanged, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appKey"] = appKey,
            ["category"] = category,
            ["confidence"] = confidence
        });

    private static FeatureSignal AppDwellEvent(DateTimeOffset ts, string appKey, long durationMs, string reason = "switch") =>
        new(ts, SignalEventType.AppDwell, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appKey"] = appKey,
            ["durationMs"] = durationMs.ToString(),
            ["reason"] = reason,
            ["dwellReason"] = reason,
            ["category"] = "IDE",
            ["confidence"] = "high"
        });

    private static FeatureSignal SystemTick(DateTimeOffset ts) =>
        new(ts, SignalEventType.SystemResourceTick, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cpu_available"] = "true", ["cpu_pct"] = "10"
        });

    [Fact]
    public void InjectHeartbeats_EmptyInput_ReturnsEmpty()
    {
        var result = AppDwellReplayPreprocessor.InjectHeartbeats(new List<FeatureSignal>());
        Assert.Empty(result);
    }

    [Fact]
    public void InjectHeartbeats_NoAppEvents_NoHeartbeats()
    {
        var signals = new List<FeatureSignal>
        {
            SystemTick(T0),
            SystemTick(T0.AddSeconds(10)),
            SystemTick(T0.AddSeconds(20))
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, s => s.Type == SignalEventType.AppFocusHeartbeat);
    }

    [Fact]
    public void InjectHeartbeats_ShortDwell_NoHeartbeats()
    {
        // Dwell only 14s — shorter than the 15s interval
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(14), "app-a", 14_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        Assert.DoesNotContain(result, s => s.Type == SignalEventType.AppFocusHeartbeat);
    }

    [Fact]
    public void InjectHeartbeats_45sDwell_ProducesTwoHeartbeatsAt15And30()
    {
        // Dwell from T0 to T0+45s — heartbeats expected at T0+15 and T0+30 (not at T0+45 which equals end)
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(45), "app-a", 45_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(2, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(30), heartbeats[1].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_HeartbeatsCarryCorrectPayload()
    {
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a", "Browser", "high"),
            AppDwellEvent(T0.AddSeconds(30), "app-a", 30_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var hb = Assert.Single(result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat));
        Assert.Equal("app-a", hb.Payload["appKey"]);
        Assert.Equal("Browser", hb.Payload["category"]);
        Assert.Equal("high", hb.Payload["confidence"]);
        Assert.Equal(T0.ToString("O"), hb.Payload["dwellStartUtc"]);
    }

    [Fact]
    public void InjectHeartbeats_DwellStartUtc_IsOpeningForegroundChangedTimestamp()
    {
        // dwellStartUtc must equal ForegroundAppChanged.TimestampUtc, not the heartbeat timestamp
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(60), "app-a", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.All(heartbeats, hb =>
        {
            Assert.True(DateTimeOffset.TryParse(hb.Payload["dwellStartUtc"], null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed));
            Assert.Equal(T0, parsed);
        });
    }

    [Fact]
    public void InjectHeartbeats_TwoConsecutiveApps_EachDwellGetsHeartbeats()
    {
        // App A: T0 to T0+30s — 1 heartbeat at T0+15
        // App B: T0+30s to T0+90s — 3 heartbeats at T0+45, T0+60, T0+75
        // In production the collector always emits AppDwell before ForegroundAppChanged on a switch.
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a", "IDE", "high"),
            AppDwellEvent(T0.AddSeconds(30), "app-a", 30_000),
            ForegroundChanged(T0.AddSeconds(30), "app-b", "Browser", "high"),
            AppDwellEvent(T0.AddSeconds(90), "app-b", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(4, heartbeats.Count);

        var aHeartbeats = heartbeats.Where(h => h.Payload["appKey"] == "app-a").ToList();
        var bHeartbeats = heartbeats.Where(h => h.Payload["appKey"] == "app-b").ToList();
        Assert.Single(aHeartbeats);
        Assert.Equal(3, bHeartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), aHeartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(45), bHeartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(60), bHeartbeats[1].TimestampUtc);
        Assert.Equal(T0.AddSeconds(75), bHeartbeats[2].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_UnclosedDwellAtEndOfFile_HeartbeatsUpToLastSignal()
    {
        // No AppDwell — dwell still open when file ends; last signal at T0+60s
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            SystemTick(T0.AddSeconds(60))
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        // Heartbeats at T0+15, T0+30, T0+45 (T0+60 == lastSignalTs, excluded by strict <)
        Assert.Equal(3, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(30), heartbeats[1].TimestampUtc);
        Assert.Equal(T0.AddSeconds(45), heartbeats[2].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_OutputIsSortedByTimestamp()
    {
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            SystemTick(T0.AddSeconds(5)),
            SystemTick(T0.AddSeconds(20)),
            AppDwellEvent(T0.AddSeconds(60), "app-a", 60_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        for (var i = 1; i < result.Count; i++)
        {
            Assert.True(result[i].TimestampUtc >= result[i - 1].TimestampUtc,
                $"Out of order at index {i}: {result[i - 1].TimestampUtc} > {result[i].TimestampUtc}");
        }
    }

    [Fact]
    public void InjectHeartbeats_UnrelatedAppDwell_DoesNotCloseCurrentDwell()
    {
        // AppDwell for "app-x" should not close the open dwell for "app-a"
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(20), "app-x", 20_000),  // unrelated app
            AppDwellEvent(T0.AddSeconds(45), "app-a", 45_000)    // real close
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        // Expect heartbeats at T0+15 and T0+30 (dwell is 0-45s)
        Assert.Equal(2, heartbeats.Count);
        Assert.All(heartbeats, hb => Assert.Equal("app-a", hb.Payload["appKey"]));
    }

    [Fact]
    public void InjectHeartbeats_CrashGap_HeartbeatsStopAfterLivenessWindowExpires()
    {
        // ForegroundAppChanged at T0, then agent crashes — next real signal is 2 hours later.
        // No AppDwell emitted (crash), so the implicit-close path applies liveness gating.
        // ForegroundAppChanged counts as a real signal, so heartbeats emit while T0 is within
        // the 60s liveness window: T0+15 through T0+60 (4 total). No phantom gap rows.
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            SystemTick(T0.AddHours(2))
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(4, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(60), heartbeats[^1].TimestampUtc);
        Assert.All(heartbeats, hb => Assert.True(hb.TimestampUtc <= T0.AddSeconds(60)));
    }

    [Fact]
    public void InjectHeartbeats_ClosedDwell_AlwaysGeneratesHeartbeatsRegardlessOfOtherSignals()
    {
        // AppDwell-confirmed dwells always get heartbeats even with no SystemResourceTick,
        // because the AppDwell itself proves the dwell was real focus.
        var signals = new List<FeatureSignal>
        {
            ForegroundChanged(T0, "app-a"),
            AppDwellEvent(T0.AddSeconds(45), "app-a", 45_000)
        };

        var result = AppDwellReplayPreprocessor.InjectHeartbeats(signals);

        var heartbeats = result.Where(s => s.Type == SignalEventType.AppFocusHeartbeat).ToList();
        Assert.Equal(2, heartbeats.Count);
        Assert.Equal(T0.AddSeconds(15), heartbeats[0].TimestampUtc);
        Assert.Equal(T0.AddSeconds(30), heartbeats[1].TimestampUtc);
    }

    [Fact]
    public void InjectHeartbeats_Integration_SustainedFocusWindowGetsNonZeroAppFeatures()
    {
        // Simulates 2 minutes of single-app focus with no AppDwell (trailing-edge never fired).
        // After heartbeat injection, AppFeatureAggregator should produce non-zero features.
        var aggregator = new AppFeatureAggregator();
        var windowStart = T0.AddMinutes(1);
        var window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));

        // Raw signals: ForegroundAppChanged at T0, then system ticks every 2s, no AppDwell
        var rawSignals = new List<FeatureSignal> { ForegroundChanged(T0, "app-a", "IDE", "high") };
        for (var sec = 2; sec <= 120; sec += 2)
        {
            rawSignals.Add(SystemTick(T0.AddSeconds(sec)));
        }

        var augmented = AppDwellReplayPreprocessor.InjectHeartbeats(rawSignals);

        // Context = events in [windowStart - 90s, windowStart + 60s)
        var historyStart = windowStart - TimeSpan.FromSeconds(90);
        var context = augmented
            .Where(s => s.TimestampUtc >= historyStart && s.TimestampUtc < window.EndUtc)
            .ToList();

        var result = aggregator.ExtractFeatures(context, window);

        Assert.Equal(1.0, result.Features["app_top1_share"], 3);
        Assert.Equal(1.0, result.Features["has_app_data"], 3);
        Assert.True(result.Features["cat_ide_ratio"] > 0.0);
    }

    [Fact]
    public void InjectHeartbeats_CalledBeforeWindowSlicing_HeartbeatsVisibleInAllProfiles()
    {
        // Verifies the preprocessor produces heartbeats that fall within the context window
        // for BOTH W60S30 (historyStart = -90s) and W30S15 (historyStart = -45s) profiles.
        var windowStart = T0.AddMinutes(5);

        // ForegroundAppChanged at T0 — 5 minutes before the target window
        var rawSignals = new List<FeatureSignal> { ForegroundChanged(T0, "app-a", "IDE", "high") };
        for (var sec = 2; sec <= 360; sec += 2)
            rawSignals.Add(SystemTick(T0.AddSeconds(sec)));

        var augmented = AppDwellReplayPreprocessor.InjectHeartbeats(rawSignals);

        // For W60S30: historyStart = windowStart - 90s
        var w60HistoryStart = windowStart - TimeSpan.FromSeconds(90);
        var w60Window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(60));
        var w60Context = augmented
            .Where(s => s.TimestampUtc >= w60HistoryStart && s.TimestampUtc < w60Window.EndUtc)
            .ToList();
        Assert.Contains(w60Context, s => s.Type == SignalEventType.AppFocusHeartbeat);

        // For W30S15: historyStart = windowStart - 45s
        var w30HistoryStart = windowStart - TimeSpan.FromSeconds(45);
        var w30Window = new SlidingWindow(windowStart, windowStart + TimeSpan.FromSeconds(30));
        var w30Context = augmented
            .Where(s => s.TimestampUtc >= w30HistoryStart && s.TimestampUtc < w30Window.EndUtc)
            .ToList();
        Assert.Contains(w30Context, s => s.Type == SignalEventType.AppFocusHeartbeat);
    }
}
