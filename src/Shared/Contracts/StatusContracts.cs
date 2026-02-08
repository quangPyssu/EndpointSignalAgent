using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

/// <summary>
/// Status query request sent to backend to check device status.
/// Corresponds to Rust backend's StatusQuery struct.
/// </summary>
public sealed record StatusRequest(
    [property: JsonPropertyName("id")] string DeviceId
);

/// <summary>
/// Status response received from backend.
/// Corresponds to Rust backend's StatusResponse struct.
/// Status can be values like: "allow", "deny", "challenge", etc.
/// </summary>
public sealed record StatusResponse(
    [property: JsonPropertyName("status")] string Status
);
