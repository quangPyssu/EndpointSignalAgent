// src/Shared/Contracts/EnrollmentContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record EnrollRequest(
    [property: JsonPropertyName("device_id")]    string? DeviceId,
    [property: JsonPropertyName("hostname")]     string? Hostname,
    [property: JsonPropertyName("os")]           string? Os,
    [property: JsonPropertyName("agent_version")] string? AgentVersion
);

public sealed record EnrollResponse(
    [property: JsonPropertyName("device_id")]      string DeviceId,
    [property: JsonPropertyName("token")]          string Token,
    [property: JsonPropertyName("report_seconds")] int ReportSeconds
);
