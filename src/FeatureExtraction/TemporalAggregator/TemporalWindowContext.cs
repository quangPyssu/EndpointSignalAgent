using EndpointSignalAgent.FeatureExtraction.SignalAggregator;

namespace EndpointSignalAgent.FeatureExtraction.TemporalAggregator;

internal sealed record TemporalWindowContext
{
    public required DateTimeOffset WindowEnd    { get; init; }
    public required DateTimeOffset SessionStart { get; init; }

    // [max(SessionStart, WindowEnd-300s), WindowEnd), ordered ascending
    public required IReadOnlyList<FeatureSignal> H300Signals    { get; init; }
    // [max(SessionStart, WindowEnd-600s), WindowEnd), ordered ascending
    public required IReadOnlyList<FeatureSignal> H600Signals    { get; init; }
    // [SessionStart, WindowEnd), ordered ascending — for state reconstruction
    public required IReadOnlyList<FeatureSignal> SessionSignals { get; init; }

    public double H300ValidSec
    {
        get
        {
            var h300Start = WindowEnd.AddSeconds(-300);
            var effective = h300Start > SessionStart ? h300Start : SessionStart;
            return Math.Max(0.0, (WindowEnd - effective).TotalSeconds);
        }
    }

    public double H600ValidSec
    {
        get
        {
            var h600Start = WindowEnd.AddSeconds(-600);
            var effective = h600Start > SessionStart ? h600Start : SessionStart;
            return Math.Max(0.0, (WindowEnd - effective).TotalSeconds);
        }
    }

    public static TemporalWindowContext Build(
        DateTimeOffset windowEnd,
        DateTimeOffset sessionStart,
        IReadOnlyList<FeatureSignal> allSessionSignals)
    {
        var h300Effective = Max(windowEnd.AddSeconds(-300), sessionStart);
        var h600Effective = Max(windowEnd.AddSeconds(-600), sessionStart);

        return new TemporalWindowContext
        {
            WindowEnd    = windowEnd,
            SessionStart = sessionStart,
            H300Signals  = allSessionSignals
                .Where(s => s.TimestampUtc >= h300Effective && s.TimestampUtc < windowEnd)
                .ToList(),
            H600Signals  = allSessionSignals
                .Where(s => s.TimestampUtc >= h600Effective && s.TimestampUtc < windowEnd)
                .ToList(),
            SessionSignals = allSessionSignals
                .Where(s => s.TimestampUtc >= sessionStart && s.TimestampUtc < windowEnd)
                .ToList()
        };
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
