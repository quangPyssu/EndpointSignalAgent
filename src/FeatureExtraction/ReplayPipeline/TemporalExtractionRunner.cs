using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.ReplayPipeline;

internal sealed class TemporalExtractionRunner
{
    private readonly TemporalFeatureExtractor _extractor = new();

    /// <summary>
    /// Enumerates windows for <paramref name="profile"/> that are fully contained within
    /// [<paramref name="sessionStart"/>, <paramref name="sessionEnd"/>] and returns one
    /// temporal feature dictionary per window.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset WindowEnd, Dictionary<string, double> Features)> Run(
        DateTimeOffset sessionStart,
        DateTimeOffset sessionEnd,
        IReadOnlyList<FeatureSignal> sessionSignals,
        WindowProfile profile)
    {
        // Align first window start to step grid, then enumerate forward
        var firstStart = SlidingWindowing.AlignToStepUtc(sessionStart, profile.SlideSec);
        if (firstStart < sessionStart)
            firstStart = firstStart.AddSeconds(profile.SlideSec); // step forward to first valid start

        var lastStart = sessionEnd.AddSeconds(-profile.WindowSizeSec);
        lastStart = SlidingWindowing.AlignToStepUtc(lastStart, profile.SlideSec);

        var results = new List<(DateTimeOffset, Dictionary<string, double>)>();

        foreach (var window in SlidingWindowing.EnumerateWindowStarts(
            firstStart, lastStart, profile.WindowSizeSec, profile.SlideSec))
        {
            if (window.StartUtc < sessionStart || window.EndUtc > sessionEnd) continue;

            var ctx      = TemporalWindowContext.Build(window.EndUtc, sessionStart, sessionSignals);
            var features = _extractor.Extract(ctx);
            results.Add((window.EndUtc, features));
        }

        return results;
    }
}
