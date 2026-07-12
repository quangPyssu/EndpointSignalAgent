// src/Shared/Contracts/StatusContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

// Nullability here must mirror the gateway's actual response shape
// (backend/api-gateway/src/http/handlers/status.rs StatusResponse): the
// cold-start case (no decision stored yet for a device, e.g. a freshly
// enrolled device or one with no registered model) returns device_id,
// model_version, window_start_ts, and decided_at_ms all as JSON null.
public sealed record StatusDecision(
    [property: JsonPropertyName("device_id")]       string? DeviceId,
    [property: JsonPropertyName("label")]           string Label,
    [property: JsonPropertyName("score")]           double Score,
    [property: JsonPropertyName("reason")]          string Reason,
    [property: JsonPropertyName("model_version")]   string? ModelVersion,
    [property: JsonPropertyName("window_start_ts")] long? WindowStartTs,
    [property: JsonPropertyName("decided_at_ms")]   long? DecidedAtMs,
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
