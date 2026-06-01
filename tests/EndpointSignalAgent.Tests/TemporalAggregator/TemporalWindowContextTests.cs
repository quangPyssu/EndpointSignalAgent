using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalWindowContextTests
{
    [Fact]
    public void H300ValidSec_ClipsToSessionStart_WhenSessionYoungerThan300s()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(120); // session only 120 s old

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        Assert.Equal(120.0, ctx.H300ValidSec, precision: 1);
        Assert.Equal(120.0, ctx.H600ValidSec, precision: 1);
    }

    [Fact]
    public void H300ValidSec_Returns300_WhenSessionOlderThan300s()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = sessionStart.AddSeconds(400);

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, Array.Empty<FeatureSignal>());

        Assert.Equal(300.0, ctx.H300ValidSec, precision: 1);
        Assert.Equal(400.0, ctx.H600ValidSec, precision: 1); // session only 400 s, h600 clips to 400
    }

    [Fact]
    public void Build_SlicesSignalsIntoCorrectHorizons()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");
        // h300 = [10:55:00, 11:00:00); h600 = [10:50:00, 11:00:00)

        var signals = new[]
        {
            E("2026-06-01T10:56:00Z", SignalEventType.ForegroundAppChanged), // in h300
            E("2026-06-01T10:52:00Z", SignalEventType.ForegroundAppChanged), // h600 only
            E("2026-06-01T10:49:00Z", SignalEventType.ForegroundAppChanged), // outside both, in session
        };

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        Assert.Single(ctx.H300Signals);
        Assert.Equal(2, ctx.H600Signals.Count);
        Assert.Equal(3, ctx.SessionSignals.Count);
    }

    [Fact]
    public void Build_ExcludesEventsAtOrAfterWindowEnd()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var windowEnd    = T("2026-06-01T11:00:00Z");

        var signals = new[]
        {
            E("2026-06-01T11:00:00Z", SignalEventType.ForegroundAppChanged), // at WindowEnd — excluded
            E("2026-06-01T10:59:59Z", SignalEventType.ForegroundAppChanged), // just before — included
        };

        var ctx = TemporalWindowContext.Build(windowEnd, sessionStart, signals);

        Assert.Single(ctx.H300Signals);
    }

    // --- helpers ---
    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static FeatureSignal E(string ts, SignalEventType type, Dictionary<string, string>? p = null) =>
        new(T(ts), type, (IReadOnlyDictionary<string, string>)(p ?? new Dictionary<string, string>()));
}
