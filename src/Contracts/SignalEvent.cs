using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

/// <summary>
/// Internal signal event structure representing a single collected signal.
/// This is serialized to JSON and sent in the Data field of SignalBatchRequest.
/// Not sent directly to backend.
/// </summary>
public sealed record SignalEvent(
    [property: JsonPropertyName("ts")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("type")] SignalEventType Type,
    [property: JsonPropertyName("payload")] Dictionary<string, string> Payload
);

/// <summary>
/// Types of signals that can be collected by the agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalEventType
{
    /// <summary>Unknown or unhandled signal type</summary>
    Unknown = 0,

    /// <summary>Periodic heartbeat signal to indicate agent is alive</summary>
    Heartbeat,

    /// <summary>User locked their Windows session</summary>
    SessionLock,

    /// <summary>User unlocked their Windows session</summary>
    SessionUnlock,

    /// <summary>User idle time sample (captured periodically)</summary>
    IdleSample,

    /// <summary>User switched to a different foreground application</summary>
    ForegroundAppChanged,

    /// <summary>WiFi network SSID hash (for location tracking)</summary>
    WifiSsidHash,

    /// <summary>VPN connection state change</summary>
    VpnState
}

/// <summary>
/// Helper class for parsing SignalEventType from strings.
/// </summary>
public static class SignalEventTypeParser
{
    /// <summary>
    /// Attempts to parse a string into a SignalEventType enum value.
    /// </summary>
    /// <param name="value">The string value to parse</param>
    /// <param name="eventType">The parsed SignalEventType if successful</param>
    /// <returns>True if parsing was successful, false otherwise</returns>
    public static bool TryParse(string value, out SignalEventType eventType)
    {
        return Enum.TryParse<SignalEventType>(value, ignoreCase: true, out eventType);
    }
}
