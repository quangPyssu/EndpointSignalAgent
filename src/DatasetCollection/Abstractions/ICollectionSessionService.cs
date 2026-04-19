using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface ICollectionSessionService
{
    CollectionSessionRecord? CurrentSession { get; }
    Task<int> CloseStaleSessionsAsync(string? notes, string initiatedBy, CancellationToken ct);
    Task<CollectionSessionRecord> StartSessionAsync(string sessionLabel, bool normalOnly, string? notes, string initiatedBy, CancellationToken ct);
    Task<CollectionSessionRecord?> PauseSessionAsync(string initiatedBy, CancellationToken ct);
    Task<CollectionSessionRecord?> ResumeSessionAsync(string initiatedBy, CancellationToken ct);
    Task<CollectionSessionRecord?> EndSessionAsync(string? notes, string initiatedBy, CancellationToken ct);
    Task<IReadOnlyList<CollectionSessionRecord>> GetSessionsAsync(CancellationToken ct);
}
