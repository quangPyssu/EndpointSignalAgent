namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record AbnormalAnnotationRecord(
    string AnnotationId,
    string SessionId,
    string SegmentType,
    string ScenarioCode,
    string ScenarioLabel,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string InitiatedBy,
    double Confidence,
    string? Notes,
    bool IsComplete,
    DateTimeOffset UpdatedAtUtc);
