using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

/// <summary>
/// Device enrollment request - sent to backend during device registration.
/// Corresponds to Rust backend's DeviceInfo struct.
/// </summary>
public sealed record EnrollRequest(
    [property: JsonPropertyName("name")] string DeviceName
);

/// <summary>
/// Device enrollment response - received from backend after successful registration.
/// Corresponds to Rust backend's EnrollResponse struct.
/// </summary>
public sealed record EnrollResponse(
    [property: JsonPropertyName("id")] string DeviceId
);
