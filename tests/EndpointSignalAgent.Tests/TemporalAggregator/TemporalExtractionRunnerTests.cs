using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.ReplayPipeline;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;
using Xunit;

namespace EndpointSignalAgent.Tests.TemporalAggregator;

public sealed class TemporalExtractionRunnerTests
{
    [Fact]
    public void Run_ProducesOneRowPerWindowInSession()
    {
        // W60S30 profile: windows every 30 s, each 60 s wide.
        // Session: [T+0, T+300). Windows that fit: start at T+0 through T+240 (end at T+60..T+300).
        // Expected count = (300-60)/30 + 1 = 9 windows.
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);

        var runner = new TemporalExtractionRunner();
        var results = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        Assert.Equal(9, results.Count);
    }

    [Fact]
    public void Run_AllRowsContainAllTemporalColumns()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);

        var runner  = new TemporalExtractionRunner();
        var results = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        foreach (var (_, features) in results)
        {
            foreach (var col in FeatureSchema.TemporalColumns)
                Assert.True(features.ContainsKey(col), $"Missing column: {col}");
        }
    }

    [Fact]
    public void Run_WindowEndsAreAlignedToProfile()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);
        var runner       = new TemporalExtractionRunner();
        var results      = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        // W60S30: windows aligned to 30 s multiples from Unix epoch
        foreach (var (windowEnd, _) in results)
        {
            var unixSec = windowEnd.ToUnixTimeSeconds();
            Assert.Equal(0, unixSec % 30);
        }
    }

    [Fact]
    public void Run_SessionAgeIncreasesAcrossWindows()
    {
        var sessionStart = T("2026-06-01T10:00:00Z");
        var sessionEnd   = sessionStart.AddSeconds(300);
        var runner       = new TemporalExtractionRunner();
        var results      = runner.Run(sessionStart, sessionEnd, Array.Empty<FeatureSignal>(), WindowProfile.W60S30);

        var ages = results.Select(r => r.Features["session_age_sec"]).ToList();
        for (int i = 1; i < ages.Count; i++)
            Assert.True(ages[i] > ages[i - 1], $"session_age_sec should increase: [{i-1}]={ages[i-1]}, [{i}]={ages[i]}");
    }

    private static DateTimeOffset T(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
