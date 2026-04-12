namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

internal readonly record struct AppOverlapSegment(
    string AppKey,
    string Category,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long OverlapMs,
    bool ConfidenceHigh);

internal readonly record struct SessionInterval(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool Locked,
    SessionDisplayState DisplayState,
    bool ScreenSaverOn,
    bool PresenceKnown,
    bool PresenceAway,
    bool IdleKnown,
    int IdleBucketSec)
{
    public long DurationMs => Math.Max(0L, (long)(EndUtc - StartUtc).TotalMilliseconds);
}

internal enum SessionDisplayState
{
    Unknown = 0,
    Off = 1,
    Dimmed = 2,
    On = 3
}

internal sealed record AppFeatureResult(
    Dictionary<string, double> Features,
    double AppSwitchCount,
    IReadOnlyList<AppOverlapSegment> OverlapSegments);

internal sealed record SessionFeatureResult(
    Dictionary<string, double> Features,
    IReadOnlyList<SessionInterval> Intervals);

internal sealed record NetworkFeatureResult(Dictionary<string, double> Features);
internal sealed record SystemResourceFeatureResult(Dictionary<string, double> Features);

internal sealed record CrossFeatureResult(Dictionary<string, double> Features);
