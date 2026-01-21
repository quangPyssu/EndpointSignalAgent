using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

public sealed record SignalEvent(
    [property: JsonPropertyName("ts")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("type")] SignalEventType Type,
    [property: JsonPropertyName("payload")] Dictionary<string, string> Payload
);


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalEventType
{
    Unknown = 0,

    Heartbeat,

    SessionLock,

    SessionUnlock,

    IdleSample,

    ForegroundAppChanged,

    AppDwell,

    AppSwitchRate,

    WifiSsidHash,

    VpnState,
    ScreenSaverOn,
    ScreenSaverOff,
    DisplayOn,
    DisplayOff,
    DisplayDimmed,

}

public static class SignalEventTypeParser
{

    public static bool TryParse(string value, out SignalEventType eventType)
    {
        return Enum.TryParse<SignalEventType>(value, ignoreCase: true, out eventType);
    }
}
