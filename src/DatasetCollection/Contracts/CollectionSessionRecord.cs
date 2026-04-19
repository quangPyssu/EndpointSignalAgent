namespace EndpointSignalAgent.DatasetCollection.Contracts;

public sealed record CollectionSessionRecord(
    string SessionId,
    string ParticipantId,
    string StudyId,
    string ProtocolVersion,
    string DeviceId,
    string AgentVersion,
    string Mode,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string SessionLabel,
    bool NormalOnly,
    string? Notes,
    string State,
    DateTimeOffset UpdatedAtUtc);
