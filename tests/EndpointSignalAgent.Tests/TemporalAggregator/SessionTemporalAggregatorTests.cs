using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class SessionTemporalAggregatorTests
{
    [Fact]
    public void ActiveWorkRatioH300_IsOne_WhenDisplayOnAndUnlocked()
    {
        // Session started 700 s ago. h300 window = last 300 s. Display on, never locked.
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),  SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(2),  SignalEventType.SessionUnlock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(1.0, features["active_work_ratio_h300"], precision: 3);
    }

    [Fact]
    public void ActiveWorkRatioH300_IsZero_WhenLockedEntireH300()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1), SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(2), SignalEventType.SessionLock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(0.0, features["active_work_ratio_h300"], precision: 3);
    }

    [Fact]
    public void ActiveWorkStreak_IsZero_WhenCurrentlyLocked()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),   SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(500), SignalEventType.SessionLock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(0.0, features["active_work_streak_sec"]);
    }

    [Fact]
    public void ActiveWorkStreak_ReflectsDurationSinceLockEnd()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        // Lock from t=200 to t=500, then unlocked and display on until window_end
        var signals = new[]
        {
            E(sessionStart.AddSeconds(1),   SignalEventType.DisplayOn),
            E(sessionStart.AddSeconds(200), SignalEventType.SessionLock),
            E(sessionStart.AddSeconds(500), SignalEventType.SessionUnlock),
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(200.0, features["active_work_streak_sec"], precision: 1); // 700-500=200 s active
    }

    [Fact]
    public void TimeSinceUnlock_IsCappedAt600()
    {
        // Last unlock was 800 s before window_end → capped at 600
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(1000);
        var signals = new[]
        {
            E(sessionStart.AddSeconds(200), SignalEventType.SessionUnlock), // 800 s before windowEnd
        };
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_unlock_sec"]);
    }

    [Fact]
    public void TimeSinceUnlock_Is600_WhenNoUnlockObserved()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(700);
        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        var features = Run(ctx);

        Assert.Equal(600.0, features["time_since_unlock_sec"]);
    }

    private static Dictionary<string, double> Run(TemporalWindowContext ctx) =>
        new SessionTemporalAggregator().Aggregate(ctx)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(DateTimeOffset ts, SignalEventType type,
        Dictionary<string, string>? p = null) =>
        new(ts, type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
