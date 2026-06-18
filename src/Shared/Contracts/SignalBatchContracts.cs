// src/Shared/Contracts/SignalBatchContracts.cs
using System.Text.Json.Serialization;

namespace EndpointSignalAgent.Shared.Contracts;

public sealed record SignalBatchRequest(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("signals")]   IReadOnlyList<SignalEvent> Signals
);

public sealed record SignalBatchResponse(
    [property: JsonPropertyName("accepted")] int Accepted
);
