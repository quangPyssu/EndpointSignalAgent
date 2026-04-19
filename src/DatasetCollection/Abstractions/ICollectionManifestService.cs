using EndpointSignalAgent.DatasetCollection.Contracts;

namespace EndpointSignalAgent.DatasetCollection.Abstractions;

public interface ICollectionManifestService
{
    Task SaveSessionAsync(CollectionSessionRecord session, CancellationToken ct);
    Task SaveAnnotationsAsync(string sessionId, IReadOnlyList<AbnormalAnnotationRecord> annotations, CancellationToken ct);
    Task SaveProgressAsync(ProgressStateRecord progress, CancellationToken ct);
    Task<IReadOnlyList<CollectionSessionRecord>> LoadSessionsAsync(CancellationToken ct);
    Task<IReadOnlyList<AbnormalAnnotationRecord>> LoadAnnotationsAsync(string sessionId, CancellationToken ct);
    Task<ProgressStateRecord?> LoadProgressAsync(CancellationToken ct);
    Task SaveStudyManifestAsync(object payload, CancellationToken ct);
    Task SaveParticipantManifestAsync(object payload, CancellationToken ct);
}
