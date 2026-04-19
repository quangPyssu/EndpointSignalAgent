using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class AbnormalTaggingService : IAbnormalTaggingService
{
    private readonly DatasetCollectionOptions _options;
    private readonly ICollectionSessionService _sessionService;
    private readonly ICollectionManifestService _manifestService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<AbnormalAnnotationRecord>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private AbnormalAnnotationRecord? _openAnnotation;

    public AbnormalTaggingService(
        IOptions<DatasetCollectionOptions> options,
        ICollectionSessionService sessionService,
        ICollectionManifestService manifestService)
    {
        _options = options.Value;
        _sessionService = sessionService;
        _manifestService = manifestService;
    }

    public async Task<AbnormalAnnotationRecord> StartAbnormalSegmentAsync(string scenarioCode, string scenarioLabel, string initiatedBy, double confidence, string? notes, CancellationToken ct)
    {
        if (!_options.EnableAbnormalTagging)
        {
            throw new InvalidOperationException("Abnormal tagging is disabled by configuration.");
        }

        var session = _sessionService.CurrentSession ?? throw new InvalidOperationException("No active collection session.");

        await _gate.WaitAsync(ct);
        try
        {
            if (_openAnnotation is not null)
            {
                return _openAnnotation;
            }

            var record = new AbnormalAnnotationRecord(
                AnnotationId: Guid.NewGuid().ToString("N"),
                SessionId: session.SessionId,
                SegmentType: "abnormal",
                ScenarioCode: scenarioCode,
                ScenarioLabel: scenarioLabel,
                StartedAtUtc: DateTimeOffset.UtcNow,
                EndedAtUtc: null,
                InitiatedBy: initiatedBy,
                Confidence: confidence,
                Notes: notes,
                IsComplete: false,
                UpdatedAtUtc: DateTimeOffset.UtcNow);

            var list = await GetListAsync(session.SessionId, ct);
            list.Add(record);
            _openAnnotation = record;
            await _manifestService.SaveAnnotationsAsync(session.SessionId, list, ct);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AbnormalAnnotationRecord?> EndAbnormalSegmentAsync(string initiatedBy, string? notes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_openAnnotation is null)
            {
                return null;
            }

            var ended = _openAnnotation with
            {
                EndedAtUtc = DateTimeOffset.UtcNow,
                IsComplete = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Notes = string.IsNullOrWhiteSpace(notes) ? _openAnnotation.Notes : $"{_openAnnotation.Notes} | ended_by={initiatedBy} | {notes}"
            };

            var list = await GetListAsync(ended.SessionId, ct);
            var idx = list.FindIndex(a => a.AnnotationId == ended.AnnotationId);
            if (idx >= 0)
            {
                list[idx] = ended;
            }

            _openAnnotation = null;
            await _manifestService.SaveAnnotationsAsync(ended.SessionId, list, ct);
            return ended;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AbnormalAnnotationRecord?> MarkLastMinutesAbnormalAsync(int minutes, string scenarioCode, string scenarioLabel, string initiatedBy, double confidence, string? notes, CancellationToken ct)
    {
        var session = _sessionService.CurrentSession;
        if (session is null)
        {
            return null;
        }

        var end = DateTimeOffset.UtcNow;
        var start = end.AddMinutes(-Math.Abs(minutes));
        var record = new AbnormalAnnotationRecord(
            AnnotationId: Guid.NewGuid().ToString("N"),
            SessionId: session.SessionId,
            SegmentType: "abnormal",
            ScenarioCode: scenarioCode,
            ScenarioLabel: scenarioLabel,
            StartedAtUtc: start,
            EndedAtUtc: end,
            InitiatedBy: initiatedBy,
            Confidence: confidence,
            Notes: notes,
            IsComplete: true,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

        await _gate.WaitAsync(ct);
        try
        {
            var list = await GetListAsync(session.SessionId, ct);
            list.Add(record);
            await _manifestService.SaveAnnotationsAsync(session.SessionId, list, ct);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AbnormalAnnotationRecord>> GetAnnotationsAsync(CancellationToken ct)
    {
        var sessionId = _sessionService.CurrentSession?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        return await GetListAsync(sessionId, ct);
    }

    private async Task<List<AbnormalAnnotationRecord>> GetListAsync(string sessionId, CancellationToken ct)
    {
        if (_cache.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var loaded = (await _manifestService.LoadAnnotationsAsync(sessionId, ct)).ToList();
        _cache[sessionId] = loaded;
        return loaded;
    }
}
