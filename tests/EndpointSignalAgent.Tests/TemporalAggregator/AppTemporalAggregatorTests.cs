using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class AppTemporalAggregatorTests
{
    private readonly AppTemporalAggregator _agg = new();

    private static TemporalWindowContext Ctx(
        IReadOnlyList<FeatureSignal> signals,
        int sessionAgeSec = 700)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(sessionAgeSec);
        return TemporalWindowContext.Build(windowEnd, sessionStart, signals);
    }

    [Fact]
    public void CategoryTransitionCount_CountsDistinctCategoryChanges()
    {
        // browser→ide→ide→terminal = 2 transitions (browser→ide, ide→terminal)
        var signals = H300Signals(new[]
        {
            ("browser",  0),
            ("ide",     10),
            ("ide",     20),   // same category — not a transition
            ("terminal",30),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(2.0, features["category_transition_count_h300"]);
    }

    [Fact]
    public void CategoryTransitionEntropy_IsZero_WhenSingleTransitionPair()
    {
        // A→B, A→B, A→B — only one distinct pair → entropy = 0
        var signals = H300Signals(new[]
        {
            ("A", 0), ("B", 5), ("B", 10), ("B", 15),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["category_transition_entropy_h300"], precision: 3);
    }

    [Fact]
    public void CategoryTransitionEntropy_IsPositive_WhenMultiplePairs()
    {
        var signals = H300Signals(new[]
        {
            ("browser",  0),
            ("ide",     10),   // browser→ide
            ("terminal",20),   // ide→terminal
        });
        var features = Run(Ctx(signals));
        Assert.True(features["category_transition_entropy_h300"] > 0.0);
    }

    [Fact]
    public void AppSwitchIntervalStd_IsZero_WhenFewerThanTwoSwitches()
    {
        var signals = H300Signals(new[] { ("browser", 0) });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["app_switch_interval_std_ms_h300"]);
    }

    [Fact]
    public void AppSwitchIntervalStd_IsZero_WhenAllIntervalsSame()
    {
        // 3 switches each 10 s apart → intervals = [10000, 10000] → std = 0
        var signals = H300Signals(new[]
        {
            ("browser",   0),
            ("ide",      10),
            ("terminal", 20),
        });
        var features = Run(Ctx(signals));
        Assert.Equal(0.0, features["app_switch_interval_std_ms_h300"], precision: 1);
    }

    [Fact]
    public void AppDwellCv_IsZero_WhenNoDwellEvents()
    {
        var ctx = TemporalWindowContext.Build(
            T("2026-06-01T10:11:40Z"),
            T("2026-06-01T10:00:00Z"),
            Array.Empty<FeatureSignal>());
        var features = Run(ctx);
        Assert.Equal(0.0, features["app_dwell_cv_h300"]);
    }

    [Fact]
    public void AppDwellCv_IsRatioOfStdToMean()
    {
        // dwells: 1000 ms and 3000 ms → mean=2000, std(pop)=1000, CV=0.5
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var h300Start    = windowEnd.AddSeconds(-300);
        var signals = new[]
        {
            new FeatureSignal(h300Start.AddSeconds(10), SignalEventType.AppDwell,
                new Dictionary<string, string> { ["durationMs"] = "1000" }),
            new FeatureSignal(h300Start.AddSeconds(20), SignalEventType.AppDwell,
                new Dictionary<string, string> { ["durationMs"] = "3000" }),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);
        var features = Run(ctx);
        Assert.Equal(0.5, features["app_dwell_cv_h300"], precision: 3);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx)
    {
        var agg = new AppTemporalAggregator();
        return agg.Aggregate(ctx).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // Builds ForegroundAppChanged signals in h300 (sessionAge 700 s → h300 starts at 400 s)
    private static IReadOnlyList<FeatureSignal> H300Signals(IEnumerable<(string cat, int offsetSec)> items)
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var h300Start    = sessionStart.AddSeconds(700 - 300);
        return items.Select(i =>
            new FeatureSignal(
                h300Start.AddSeconds(i.offsetSec),
                SignalEventType.ForegroundAppChanged,
                new Dictionary<string, string> { ["category"] = i.cat }))
            .ToList<FeatureSignal>();
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
