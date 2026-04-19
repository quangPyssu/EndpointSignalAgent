using System.Reflection;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class CollectionSessionService : ICollectionSessionService
{
    private readonly DatasetCollectionOptions _options;
    private readonly IAgentIdentity _agentIdentity;
    private readonly ICollectionManifestService _manifestService;
    private readonly ILogger<CollectionSessionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CollectionSessionRecord? CurrentSession { get; private set; }

    public CollectionSessionService(
        IOptions<DatasetCollectionOptions> options,
        IAgentIdentity agentIdentity,
        ICollectionManifestService manifestService,
        ILogger<CollectionSessionService> logger)
    {
        _options = options.Value;
        _agentIdentity = agentIdentity;
        _manifestService = manifestService;
        _logger = logger;
    }

    public async Task<CollectionSessionRecord> StartSessionAsync(string sessionLabel, bool normalOnly, string? notes, string initiatedBy, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentSession is { State: "Running" or "Paused" })
            {
                return CurrentSession;
            }

            var now = DateTimeOffset.UtcNow;
            var session = new CollectionSessionRecord(
                SessionId: Guid.NewGuid().ToString("N"),
                ParticipantId: _options.ParticipantId,
                StudyId: _options.StudyId,
                ProtocolVersion: _options.ProtocolVersion,
                DeviceId: _agentIdentity.DeviceId,
                AgentVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                Mode: AgentModes.DatasetCollection,
                StartedAtUtc: now,
                EndedAtUtc: null,
                SessionLabel: string.IsNullOrWhiteSpace(sessionLabel) ? $"session-{now:yyyyMMdd-HHmmss}" : sessionLabel.Trim(),
                NormalOnly: normalOnly,
                Notes: MergeNotes(notes, $"started_by={initiatedBy}"),
                State: "Running",
                UpdatedAtUtc: now);

            CurrentSession = session;
            await _manifestService.SaveSessionAsync(session, ct);
            await WriteStudyManifestsAsync(ct);
            _logger.LogInformation("Dataset session started: {SessionId}", session.SessionId);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<CollectionSessionRecord?> PauseSessionAsync(string initiatedBy, CancellationToken ct)
        => TransitionAsync("Paused", initiatedBy, null, ct);

    public Task<CollectionSessionRecord?> ResumeSessionAsync(string initiatedBy, CancellationToken ct)
        => TransitionAsync("Running", initiatedBy, null, ct);

    public Task<CollectionSessionRecord?> EndSessionAsync(string? notes, string initiatedBy, CancellationToken ct)
        => TransitionAsync("Completed", initiatedBy, notes, ct, setEndedAt: true);

    public async Task<int> CloseStaleSessionsAsync(string? notes, string initiatedBy, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var sessions = await _manifestService.LoadSessionsAsync(ct);
            var staleSessions = sessions
                .Where(s => (string.Equals(s.State, "Running", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(s.State, "Paused", StringComparison.OrdinalIgnoreCase))
                            && !s.EndedAtUtc.HasValue)
                .ToList();

            if (staleSessions.Count == 0)
            {
                return 0;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var stale in staleSessions)
            {
                var recovered = stale with
                {
                    State = "Completed",
                    EndedAtUtc = now,
                    UpdatedAtUtc = now,
                    Notes = MergeNotes(stale.Notes, MergeNotes(notes, $"state=Completed,by={initiatedBy}"))
                };

                await _manifestService.SaveSessionAsync(recovered, ct);
            }

            if (CurrentSession is not null && staleSessions.Any(s => string.Equals(s.SessionId, CurrentSession.SessionId, StringComparison.Ordinal)))
            {
                CurrentSession = null;
            }

            return staleSessions.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<CollectionSessionRecord>> GetSessionsAsync(CancellationToken ct)
        => _manifestService.LoadSessionsAsync(ct);

    private async Task<CollectionSessionRecord?> TransitionAsync(string state, string initiatedBy, string? notes, CancellationToken ct, bool setEndedAt = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (CurrentSession is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var updated = CurrentSession with
            {
                State = state,
                UpdatedAtUtc = now,
                EndedAtUtc = setEndedAt ? now : CurrentSession.EndedAtUtc,
                Notes = MergeNotes(CurrentSession.Notes, MergeNotes(notes, $"state={state},by={initiatedBy}"))
            };

            CurrentSession = state == "Completed" ? null : updated;
            await _manifestService.SaveSessionAsync(updated, ct);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteStudyManifestsAsync(CancellationToken ct)
    {
        await _manifestService.SaveStudyManifestAsync(new
        {
            _options.StudyId,
            _options.ProtocolVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, ct);

        await _manifestService.SaveParticipantManifestAsync(new
        {
            _options.ParticipantId,
            _options.StudyId,
            _options.ProtocolVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        }, ct);
    }

    private static string? MergeNotes(string? left, string? right)
    {
        var parts = new[] { left, right }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());
        var merged = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(merged) ? null : merged;
    }
}
