using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

/// <summary>
/// Signal batch request sent to backend.
/// Corresponds to Rust backend's SendRequest struct.
/// The Data field contains JSON-serialized array of SignalEvent objects.
/// </summary>
public sealed record SignalBatchRequest(
    [property: JsonPropertyName("id")] string DeviceId,
    [property: JsonPropertyName("data")] string Data
);

/// <summary>
/// Signal batch response received from backend.
/// Corresponds to Rust backend's SendResponse struct.
/// </summary>
public sealed record SignalBatchResponse(
    [property: JsonPropertyName("success")] bool Success
);
