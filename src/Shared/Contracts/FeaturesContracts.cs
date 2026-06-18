// src/Shared/Contracts/FeaturesContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record FeaturesRequest(
    [property: JsonPropertyName("device_id")]       string DeviceId,
    [property: JsonPropertyName("feature_version")] string FeatureVersion,
    [property: JsonPropertyName("window_sec")]      int WindowSec,
    [property: JsonPropertyName("rows")]            IReadOnlyList<Dictionary<string, object>> Rows
);

public sealed record FeaturesResponse(
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("rejected")] IReadOnlyList<string> Rejected
);
