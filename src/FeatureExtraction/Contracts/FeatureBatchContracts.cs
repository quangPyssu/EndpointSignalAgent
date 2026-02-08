using System.Text.Json.Serialization;

namespace EndpointSignalAgent.FeatureExtraction.Contracts;

/// <summary>
/// Request to send a batch of feature rows to the backend.
/// </summary>
public sealed record FeatureBatchRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("features")] List<FeatureRowDto> Features
);

/// <summary>
/// Simplified DTO for sending feature rows (excludes internal fields like sent_flag)
/// </summary>
public sealed record FeatureRowDto(
    [property: JsonPropertyName("window_sec")] int WindowSec,
    [property: JsonPropertyName("window_start_ts")] DateTimeOffset WindowStartTs,
    [property: JsonPropertyName("feature_version")] string FeatureVersion,
    [property: JsonPropertyName("features")] Dictionary<string, object> Features
);
