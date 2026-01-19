using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchRequest(
    [property: JsonPropertyName("id")] string DeviceId,
    [property: JsonPropertyName("data")] string Data
);


public sealed record SignalBatchResponse(
    [property: JsonPropertyName("success")] bool Success
);
