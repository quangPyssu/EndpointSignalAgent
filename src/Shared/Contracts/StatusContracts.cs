// src/Shared/Contracts/StatusContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record StatusDecision(
    [property: JsonPropertyName("device_id")]       string DeviceId,
    [property: JsonPropertyName("label")]           string Label,
    [property: JsonPropertyName("score")]           double Score,
    [property: JsonPropertyName("reason")]          string Reason,
    [property: JsonPropertyName("model_version")]   string ModelVersion,
    [property: JsonPropertyName("window_start_ts")] long WindowStartTs,
    [property: JsonPropertyName("decided_at_ms")]   long DecidedAtMs,
    [property: JsonPropertyName("ttl_seconds")]     int TtlSeconds
);

public static class KnownLabels
{
    public const string Allow      = "allow";
    public const string Deny       = "deny";
    public const string Lock       = "lock";
    public const string ForceSleep = "force_sleep";
    public const string Unknown    = "unknown";
}
