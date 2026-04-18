namespace EndpointSignalAgent.FeatureExtraction.Contracts;

public sealed record WindowProfile(
    string ProfileId,
    int WindowSizeSec,
    int SlideSec,
    DateTimeOffset AlignmentOriginUtc)
{
    public static readonly WindowProfile W60S30 = new("W60_S30", 60, 30, DateTimeOffset.UnixEpoch);
    public static readonly WindowProfile W120S60 = new("W120_S60", 120, 60, DateTimeOffset.UnixEpoch);
    public static readonly WindowProfile W30S15 = new("W30_S15", 30, 15, DateTimeOffset.UnixEpoch);

    public static IReadOnlyList<WindowProfile> DefaultProfiles { get; } = new[]
    {
        W60S30,
        W120S60,
        W30S15
    };
}
