using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

/// <summary>
/// Represents a feature row built from a window of signal events.
/// Contains aggregated features extracted over a time window.
/// Stored in SQLite database with upload tracking.
/// </summary>
public sealed record FeatureRow(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("window_sec")] int WindowSec,
    [property: JsonPropertyName("window_start_ts")] DateTimeOffset WindowStartTs,
    [property: JsonPropertyName("feature_version")] string FeatureVersion,
    [property: JsonPropertyName("features")] Dictionary<string, object> Features,
    [property: JsonPropertyName("sent_flag")] bool SentFlag,
    [property: JsonPropertyName("sent_at")] DateTimeOffset? SentAt
)
{
    /// <summary>
    /// Create a new unsent feature row (for insertion)
    /// </summary>
    public static FeatureRow CreateNew(
        string deviceId,
        int windowSec,
        DateTimeOffset windowStartTs,
        string featureVersion,
        Dictionary<string, object> features)
    {
        return new FeatureRow(
            Id: 0, // Will be assigned by database
            DeviceId: deviceId,
            WindowSec: windowSec,
            WindowStartTs: windowStartTs,
            FeatureVersion: featureVersion,
            Features: features,
            SentFlag: false,
            SentAt: null
        );
    }
}

