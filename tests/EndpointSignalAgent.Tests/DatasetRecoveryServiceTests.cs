using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using EndpointSignalAgent.DatasetCollection.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Tests;

public sealed class DatasetRecoveryServiceTests
{
    [Fact]
    public async Task RecoverDanglingSessionsAsync_CompletesSessionAndOpenAbnormal()
    {
        var session = new CollectionSessionRecord(
            SessionId: "session-1",
            ParticipantId: "p1",
            StudyId: "s1",
            ProtocolVersion: "1.0",
            DeviceId: "d1",
            AgentVersion: "1",
            Mode: AgentModes.DatasetCollection,
            StartedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
            EndedAtUtc: null,
            SessionLabel: "label",
            NormalOnly: false,
            Notes: null,
            State: "Running",
            UpdatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        var annotation = new AbnormalAnnotationRecord(
            AnnotationId: "a1",
            SessionId: "session-1",
            SegmentType: "abnormal",
            ScenarioCode: "X",
            ScenarioLabel: "X",
            StartedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-20),
            EndedAtUtc: null,
            InitiatedBy: "tray",
            Confidence: 0.9,
            Notes: null,
            IsComplete: false,
            UpdatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        var manifest = new FakeManifestService([session], new Dictionary<string, IReadOnlyList<AbnormalAnnotationRecord>>
        {
            [session.SessionId] = [annotation]
        });
        var progress = new FakeProgressService();

        var service = new DatasetRecoveryService(
            Options.Create(new AgentOptions { Mode = AgentModes.DatasetCollection }),
            Options.Create(new DatasetCollectionOptions { Enabled = true }),
            manifest,
            progress,
            NullLogger<DatasetRecoveryService>.Instance);

        var recovered = await service.RecoverDanglingSessionsAsync(CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal("Completed", manifest.SavedSessions.Single().State);
        Assert.NotNull(manifest.SavedSessions.Single().EndedAtUtc);
        Assert.Contains("[recovered_after_unclean_stop=true]", manifest.SavedSessions.Single().Notes);

        var savedAnnotation = manifest.SavedAnnotations[session.SessionId].Single();
        Assert.True(savedAnnotation.IsComplete);
        Assert.NotNull(savedAnnotation.EndedAtUtc);
        Assert.Contains("[auto_closed=recovery]", savedAnnotation.Notes);
        Assert.True(progress.RecalculateCalled);
    }

    private sealed class FakeManifestService : ICollectionManifestService
    {
        private readonly IReadOnlyList<CollectionSessionRecord> _sessions;
        private readonly Dictionary<string, IReadOnlyList<AbnormalAnnotationRecord>> _annotations;

        public List<CollectionSessionRecord> SavedSessions { get; } = [];
        public Dictionary<string, IReadOnlyList<AbnormalAnnotationRecord>> SavedAnnotations { get; } = new(StringComparer.Ordinal);

        public FakeManifestService(IReadOnlyList<CollectionSessionRecord> sessions, Dictionary<string, IReadOnlyList<AbnormalAnnotationRecord>> annotations)
        {
            _sessions = sessions;
            _annotations = annotations;
        }

        public Task SaveSessionAsync(CollectionSessionRecord session, CancellationToken ct)
        {
            SavedSessions.Add(session);
            return Task.CompletedTask;
        }

        public Task SaveAnnotationsAsync(string sessionId, IReadOnlyList<AbnormalAnnotationRecord> annotations, CancellationToken ct)
        {
            SavedAnnotations[sessionId] = annotations;
            return Task.CompletedTask;
        }

        public Task SaveProgressAsync(ProgressStateRecord progress, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CollectionSessionRecord>> LoadSessionsAsync(CancellationToken ct) => Task.FromResult(_sessions);

        public Task<IReadOnlyList<AbnormalAnnotationRecord>> LoadAnnotationsAsync(string sessionId, CancellationToken ct)
            => Task.FromResult(_annotations.TryGetValue(sessionId, out var a) ? a : (IReadOnlyList<AbnormalAnnotationRecord>)[]);

        public Task<ProgressStateRecord?> LoadProgressAsync(CancellationToken ct) => Task.FromResult<ProgressStateRecord?>(null);
        public Task SaveStudyManifestAsync(object payload, CancellationToken ct) => Task.CompletedTask;
        public Task SaveParticipantManifestAsync(object payload, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeProgressService : IProgressTrackingService
    {
        public bool RecalculateCalled { get; private set; }

        public Task<ProgressStateRecord> RecalculateAsync(CancellationToken ct)
        {
            RecalculateCalled = true;
            return Task.FromResult(new ProgressStateRecord(0, 0, 0, 0, 0, 0, 0, 0, "In progress", 0, DateTimeOffset.UtcNow));
        }

        public Task<ProgressStateRecord> GetCurrentAsync(CancellationToken ct)
            => Task.FromResult(new ProgressStateRecord(0, 0, 0, 0, 0, 0, 0, 0, "In progress", 0, DateTimeOffset.UtcNow));

        public Task<ProgressTraySnapshot> GetTraySnapshotAsync(CancellationToken ct)
            => Task.FromResult(new ProgressTraySnapshot(0, 0, 0, 0, 0, "In progress"));
    }
}
