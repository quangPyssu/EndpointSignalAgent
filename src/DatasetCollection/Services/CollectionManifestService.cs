using System.Text.Json;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using EndpointSignalAgent.DatasetCollection.Storage;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class CollectionManifestService : ICollectionManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly DatasetCollectionOptions _options;
    private readonly SessionManifestStore _sessionStore;
    private readonly AnnotationStore _annotationStore;
    private readonly ProgressStateStore _progressStore;

    public CollectionManifestService(
        IOptions<DatasetCollectionOptions> options,
        SessionManifestStore sessionStore,
        AnnotationStore annotationStore,
        ProgressStateStore progressStore)
    {
        _options = options.Value;
        _sessionStore = sessionStore;
        _annotationStore = annotationStore;
        _progressStore = progressStore;
    }

    public Task SaveSessionAsync(CollectionSessionRecord session, CancellationToken ct) => _sessionStore.SaveSessionAsync(_options.ManifestRoot, session, ct);

    public Task SaveAnnotationsAsync(string sessionId, IReadOnlyList<AbnormalAnnotationRecord> annotations, CancellationToken ct)
        => _annotationStore.SaveAsync(_options.ManifestRoot, sessionId, annotations, ct);

    public Task SaveProgressAsync(ProgressStateRecord progress, CancellationToken ct)
        => _progressStore.SaveAsync(_options.ManifestRoot, progress, ct);

    public Task<IReadOnlyList<CollectionSessionRecord>> LoadSessionsAsync(CancellationToken ct)
        => _sessionStore.LoadAllSessionsAsync(_options.ManifestRoot, ct);

    public Task<IReadOnlyList<AbnormalAnnotationRecord>> LoadAnnotationsAsync(string sessionId, CancellationToken ct)
        => _annotationStore.LoadAsync(_options.ManifestRoot, sessionId, ct);

    public Task<ProgressStateRecord?> LoadProgressAsync(CancellationToken ct)
        => _progressStore.LoadAsync(_options.ManifestRoot, ct);

    public Task SaveStudyManifestAsync(object payload, CancellationToken ct)
        => SaveAtomicAsync(Path.Combine(_options.ManifestRoot, "study_manifest.json"), payload, ct);

    public Task SaveParticipantManifestAsync(object payload, CancellationToken ct)
        => SaveAtomicAsync(Path.Combine(_options.ManifestRoot, "participant_manifest.json"), payload, ct);

    private static async Task SaveAtomicAsync(string path, object payload, CancellationToken ct)
    {
        await AtomicJsonFileWriter.WriteAsync(path, payload, JsonOptions, ct);
    }
}
