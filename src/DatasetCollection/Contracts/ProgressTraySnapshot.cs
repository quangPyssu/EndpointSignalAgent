namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record ProgressTraySnapshot(
    double TotalRuntimeHours,
    double TotalActiveHours,
    int TotalSessionsCompleted,
    int ValidCollectionDays,
    double CompletionRatio,
    string CompletionStatus);
