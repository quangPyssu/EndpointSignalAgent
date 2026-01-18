namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchRequest(
    string DeviceId,
    string SessionId,
    DateTimeOffset SentAt,
    List<SignalEvent> Events
    );

public sealed record SignalEvent(
    DateTimeOffset TimestampUtc,
    string Type,
    Dictionary<string, string> Payload
);
