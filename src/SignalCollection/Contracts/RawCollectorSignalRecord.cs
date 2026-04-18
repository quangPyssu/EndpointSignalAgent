using System.Text.Json.Serialization;

namespace EndpointSignalAgent.SignalCollection.Contracts;

public enum SignalKind
{
    Event = 0,
    StateSample = 1,
    StateChange = 2,
    PreAggregated = 3
}

public sealed record RawCollectorSignalRecord(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("ts_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("recording_id")] string RecordingId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("collector")] string Collector,
    [property: JsonPropertyName("signal_type")] string SignalType,
    [property: JsonPropertyName("signal_kind")] string SignalKind,
    [property: JsonPropertyName("native_cadence_sec")] int? NativeCadenceSec,
    [property: JsonPropertyName("native_aggregation_sec")] int? NativeAggregationSec,
    [property: JsonPropertyName("collector_schema_version")] string CollectorSchemaVersion,
    [property: JsonPropertyName("payload")] Dictionary<string, string> Payload
);

public sealed record SignalProvenance(
    string Collector,
    SignalKind Kind,
    int? NativeCadenceSec,
    int? NativeAggregationSec,
    string CollectorSchemaVersion);

public static class SignalProvenanceCatalog
{
    public const string RawSchemaVersion = "raw-collector-v1";

    public static SignalProvenance Resolve(Shared.Contracts.SignalEventType type)
    {
        return type switch
        {
            Shared.Contracts.SignalEventType.ForegroundAppChanged => new("ApplicationUsageCollector", SignalKind.Event, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.AppDwell => new("ApplicationUsageCollector", SignalKind.Event, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.AppSwitchRate => new("ApplicationUsageCollector", SignalKind.PreAggregated, 1, 60, "2.0"),

            Shared.Contracts.SignalEventType.SessionLock => new("SessionStateCollector", SignalKind.Event, null, null, "2.0"),
            Shared.Contracts.SignalEventType.SessionUnlock => new("SessionStateCollector", SignalKind.Event, null, null, "2.0"),
            Shared.Contracts.SignalEventType.IdleSample => new("SessionStateCollector", SignalKind.StateSample, null, null, "2.0"),
            Shared.Contracts.SignalEventType.ScreenSaverOn => new("SessionStateCollector", SignalKind.StateChange, null, null, "2.0"),
            Shared.Contracts.SignalEventType.ScreenSaverOff => new("SessionStateCollector", SignalKind.StateChange, null, null, "2.0"),
            Shared.Contracts.SignalEventType.DisplayOn => new("SessionStateCollector", SignalKind.StateChange, null, null, "2.0"),
            Shared.Contracts.SignalEventType.DisplayOff => new("SessionStateCollector", SignalKind.StateChange, null, null, "2.0"),
            Shared.Contracts.SignalEventType.DisplayDimmed => new("SessionStateCollector", SignalKind.StateChange, null, null, "2.0"),

            Shared.Contracts.SignalEventType.VpnStateChanged => new("NetworkContextCollector", SignalKind.StateChange, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.WifiLinkChanged => new("NetworkContextCollector", SignalKind.StateChange, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.WifiSsidChanged => new("NetworkContextCollector", SignalKind.StateChange, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.LocalNetworkChanged => new("NetworkContextCollector", SignalKind.StateChange, 3, null, "2.0"),
            Shared.Contracts.SignalEventType.PublicIpBucketChanged => new("NetworkContextCollector", SignalKind.StateChange, 60, null, "2.0"),

            Shared.Contracts.SignalEventType.SystemResourceSample => new("SystemResourceCollector", SignalKind.PreAggregated, 5, null, "1.0"),
            _ => new("UnknownCollector", SignalKind.Event, null, null, "1.0")
        };
    }
}
