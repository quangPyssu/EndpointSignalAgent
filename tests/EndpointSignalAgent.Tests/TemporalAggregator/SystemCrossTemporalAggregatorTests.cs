using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class SystemCrossTemporalAggregatorTests
{
    // --- SystemTemporalAggregator ---

    [Fact]
    public void CpuHighPersistence_IsZero_WhenNoTicks()
    {
        var ctx = EmptyCtx(700);
        var features = RunSystem(ctx);
        Assert.Equal(0.0, features["cpu_high_persistence_h300"]);
    }

    [Fact]
    public void CpuHighPersistence_RatioOfHighCpuSamples()
    {
        // 3 ticks: cpu 90, 50, 90 → 2/3 high
        var ticks = H300Ticks(new[] { (90.0, 0.0, 0.0, 0.0), (50.0, 0.0, 0.0, 0.0), (90.0, 0.0, 0.0, 0.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.Equal(2.0 / 3.0, features["cpu_high_persistence_h300"], precision: 3);
    }

    [Fact]
    public void NetThroughputTrend_IsZero_WhenFewerThanTwoTicks()
    {
        var ticks = H300Ticks(new[] { (50.0, 0.0, 10.0, 5.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.Equal(0.0, features["net_throughput_trend_h300"]);
    }

    [Fact]
    public void NetThroughputTrend_IsPositive_WhenThroughputIncreases()
    {
        // Throughput goes 10, 20, 30 → slope positive
        var ticks = H300Ticks(new[] { (0.0, 0.0, 5.0, 5.0), (0.0, 0.0, 10.0, 10.0), (0.0, 0.0, 15.0, 15.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.True(features["net_throughput_trend_h300"] > 0.0);
    }

    [Fact]
    public void NetThroughputTrend_IsNegative_WhenThroughputDecreases()
    {
        var ticks = H300Ticks(new[] { (0.0, 0.0, 15.0, 15.0), (0.0, 0.0, 10.0, 10.0), (0.0, 0.0, 5.0, 5.0) });
        var ctx = CtxWithSignals(ticks, 700);
        var features = RunSystem(ctx);
        Assert.True(features["net_throughput_trend_h300"] < 0.0);
    }

    // --- CrossTemporalAggregator ---

    [Fact]
    public void ActiveResourceMismatch_IsZero_WhenNoTicks()
    {
        var ctx = EmptyCtx(700);
        var features = RunCross(ctx);
        Assert.Equal(0.0, features["active_resource_mismatch_h300"]);
    }

    [Fact]
    public void ActiveResourceMismatch_CountsHighResourceDuringInactiveTime()
    {
        // Session: display on, never unlocked → session defaults to locked → not active_work
        // 2 ticks with cpu_pct=90 (high) during non-active time → mismatch ratio = 1.0
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        var ticks = new[]
        {
            Tick(h300Start.AddSeconds(10), 90.0, 0.0, 0.0, 0.0),
            Tick(h300Start.AddSeconds(20), 90.0, 0.0, 0.0, 0.0),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, ticks);
        var features = RunCross(ctx);
        Assert.Equal(1.0, features["active_resource_mismatch_h300"], precision: 3);
    }

    [Fact]
    public void ActiveResourceMismatch_IsZero_WhenHighResourceDuringActiveWork()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        // Display on + unlocked → active_work
        var signals = new List<FeatureSignal>
        {
            new(sessionStart.AddSeconds(1),  SignalEventType.DisplayOn,
                new Dictionary<string, string>()),
            new(sessionStart.AddSeconds(2),  SignalEventType.SessionUnlock,
                new Dictionary<string, string>()),
            Tick(h300Start.AddSeconds(10), 90.0, 0.0, 0.0, 0.0),
            Tick(h300Start.AddSeconds(20), 90.0, 0.0, 0.0, 0.0),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = RunCross(ctx);
        Assert.Equal(0.0, features["active_resource_mismatch_h300"], precision: 3);
    }

    // --- helpers ---

    private static TemporalWindowContext EmptyCtx(int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return TemporalWindowContext.Build(s.AddSeconds(ageSec), s, Array.Empty<FeatureSignal>());
    }

    private static TemporalWindowContext CtxWithSignals(IReadOnlyList<FeatureSignal> signals, int ageSec)
    {
        var s = T("2026-06-01T10:00:00Z");
        return TemporalWindowContext.Build(s.AddSeconds(ageSec), s, signals);
    }

    // (cpu, mem, rx, tx) tuples — placed at evenly-spaced offsets inside h300
    private static IReadOnlyList<FeatureSignal> H300Ticks(IEnumerable<(double cpu, double mem, double rx, double tx)> items)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var h300Start    = sessionStart.AddSeconds(700 - 300);
        return items.Select((item, i) =>
            Tick(h300Start.AddSeconds(i * 5 + 1), item.cpu, item.mem, item.rx, item.tx))
            .ToList();
    }

    private static FeatureSignal Tick(DateTimeOffset ts, double cpu, double mem, double rx, double tx) =>
        new(ts, SignalEventType.SystemResourceTick,
            new Dictionary<string, string>
            {
                ["cpu_pct"]       = cpu.ToString(),
                ["mem_used_pct"]  = mem.ToString(),
                ["net_rx_kbps"]   = rx.ToString(),
                ["net_tx_kbps"]   = tx.ToString(),
                ["cpu_available"] = "true",
                ["mem_available"] = "true"
            });

    private static Dictionary<string, double> RunSystem(TemporalWindowContext ctx) =>
        new SystemTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static Dictionary<string, double> RunCross(TemporalWindowContext ctx) =>
        new CrossTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
