using System.Text.Json.Serialization;

namespace EndpointSignalAgent.FeatureExtraction.Contracts;

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
    [property: JsonPropertyName("window_profile_id")] string WindowProfileId,
    [property: JsonPropertyName("window_size_sec")] int WindowSizeSec,
    [property: JsonPropertyName("slide_sec")] int SlideSec,
    [property: JsonPropertyName("event_time_start")] DateTimeOffset EventTimeStart,
    [property: JsonPropertyName("event_time_end")] DateTimeOffset EventTimeEnd,
    [property: JsonPropertyName("extraction_run_id")] string ExtractionRunId,
    [property: JsonPropertyName("feature_schema_version")] string FeatureSchemaVersion,
    [property: JsonPropertyName("collector_schema_version")] string? CollectorSchemaVersion,
    [property: JsonPropertyName("source_counts")] Dictionary<string, int> SourceCounts,
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
        string windowProfileId,
        int windowSizeSec,
        int slideSec,
        DateTimeOffset eventTimeStart,
        DateTimeOffset eventTimeEnd,
        string extractionRunId,
        string featureSchemaVersion,
        string? collectorSchemaVersion,
        Dictionary<string, int> sourceCounts,
        Dictionary<string, object> features)
    {
        return new FeatureRow(
            Id: 0, // Will be assigned by database
            DeviceId: deviceId,
            WindowSec: windowSec,
            WindowStartTs: windowStartTs,
            FeatureVersion: featureVersion,
            WindowProfileId: windowProfileId,
            WindowSizeSec: windowSizeSec,
            SlideSec: slideSec,
            EventTimeStart: eventTimeStart,
            EventTimeEnd: eventTimeEnd,
            ExtractionRunId: extractionRunId,
            FeatureSchemaVersion: featureSchemaVersion,
            CollectorSchemaVersion: collectorSchemaVersion,
            SourceCounts: sourceCounts,
            Features: features,
            SentFlag: false,
            SentAt: null
        );
    }
}
