namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchRequest(
    string DeviceId,
    string SessionId,
    DateTimeOffset SentAt,
    IReadOnlyList<SignalEvent> Events
);

public sealed record SignalEvent(
    DateTimeOffset Ts,
    string Type,                  // e.g., "SessionLock", "AppFocus", "Network"
    Dictionary<string, string> Data
);
