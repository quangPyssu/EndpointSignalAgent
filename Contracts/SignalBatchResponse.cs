namespace EndpointSignalAgent.Contracts;

public sealed record SignalBatchResponse(
    string Decision,              // "ALLOW" | "WARN" | "STEP_UP" | "LOCK"
    int RiskScore,                // 0-100 (example)
    string? Message = null,
    int? NextReportSeconds = null
);
