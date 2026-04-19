namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record ProgressStateRecord(
    int StudySpanWeeks,
    int ValidCollectionDays,
    int TotalSessionsCompleted,
    double TotalRuntimeHours,
    double TotalActiveHours,
    int AbnormalScenariosCompleted,
    double AbnormalMinutes,
    int CoreSignalCoverageDaysOk,
    string CompletionStatus,
    double CompletionRatio,
    DateTimeOffset LastUpdatedUtc);
