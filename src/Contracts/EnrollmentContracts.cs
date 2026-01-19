using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Contracts;

public sealed record EnrollRequest(
    [property: JsonPropertyName("name")] string DeviceName
);

public sealed record EnrollResponse(
    [property: JsonPropertyName("id")] string DeviceId
);
