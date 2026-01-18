namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchResponse(
    string Decision,
    double RiskScore,
    string? Message = null,
    int? NextReportSeconds = null
);

public sealed record StatusRequest(
    string DeviceId,
    string SessionId);

public sealed record StatusResponse(
    string Decision,
    double RiskScore,
    string? Message,
    int? NextReportSeconds);
