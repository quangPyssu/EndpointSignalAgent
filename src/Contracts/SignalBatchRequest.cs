using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchRequest(
    string DeviceId,
    string SessionId,
    DateTimeOffset SentAt,
    List<SignalEvent> Events
    );

public sealed record SignalEvent(
    DateTimeOffset TimestampUtc,
    SignalEventType Type,
    Dictionary<string, string> Payload
);


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalEventType
{
    Unknown = 0,

    // MVP / skeleton
    Heartbeat,

    // Session state (what you want to start with)
    SessionLock,
    SessionUnlock,

    // Future-friendly placeholders (optional)
    IdleSample,
    ForegroundAppChanged,
    WifiSsidHash,
    VpnState
}

// parser for SignalEventType from string (if needed in future)
public static class SignalEventTypeParser
{
    public static bool TryParse(string value, out SignalEventType eventType)
    {
        return Enum.TryParse<SignalEventType>(value, ignoreCase: true, out eventType);
    }
}