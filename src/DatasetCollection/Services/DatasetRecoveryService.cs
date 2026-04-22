using System.Text.Json;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class DatasetRecoveryService : IDatasetRecoveryService
{
    private readonly AgentOptions _agentOptions;
    private readonly DatasetCollectionOptions _datasetOptions;
    private readonly ICollectionManifestService _manifestService;
    private readonly IProgressTrackingService _progressTrackingService;
    private readonly ILogger<DatasetRecoveryService> _logger;

    public DatasetRecoveryService(
        IOptions<AgentOptions> agentOptions,
        IOptions<DatasetCollectionOptions> datasetOptions,
        ICollectionManifestService manifestService,
        IProgressTrackingService progressTrackingService,
        ILogger<DatasetRecoveryService> logger)
    {
        _agentOptions = agentOptions.Value;
        _datasetOptions = datasetOptions.Value;
        _manifestService = manifestService;
        _progressTrackingService = progressTrackingService;
        _logger = logger;
    }

    public async Task<int> RecoverDanglingSessionsAsync(CancellationToken ct)
    {
        if (!AgentModes.IsDatasetCollection(_agentOptions.Mode) || !_datasetOptions.Enabled)
        {
            return 0;
        }

        var recovered = 0;
        var sessions = await _manifestService.LoadSessionsAsync(ct);
        foreach (var session in sessions.Where(IsDanglingSession))
        {
            var annotations = (await _manifestService.LoadAnnotationsAsync(session.SessionId, ct)).ToList();
            var recoveredEndedAtUtc = await ResolveRecoveredEndedAtUtcAsync(session, annotations, ct);

            var updatedAnnotations = false;
            for (var i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                if (annotation.IsComplete && annotation.EndedAtUtc.HasValue)
                {
                    continue;
                }

                annotations[i] = annotation with
                {
                    EndedAtUtc = annotation.EndedAtUtc ?? recoveredEndedAtUtc,
                    IsComplete = true,
                    UpdatedAtUtc = recoveredEndedAtUtc,
                    Notes = AppendMarker(annotation.Notes, "[auto_closed=recovery]")
                };
                updatedAnnotations = true;
            }

            var updatedSession = session with
            {
                EndedAtUtc = recoveredEndedAtUtc,
                State = "Completed",
                UpdatedAtUtc = recoveredEndedAtUtc,
                Notes = AppendMarker(session.Notes, "[recovered_after_unclean_stop=true]")
            };

            await _manifestService.SaveSessionAsync(updatedSession, ct);
            if (updatedAnnotations)
            {
                await _manifestService.SaveAnnotationsAsync(session.SessionId, annotations, ct);
            }

            recovered++;
            _logger.LogInformation("Recovered dangling dataset session {SessionId} at {RecoveredEndedAtUtc:O}", session.SessionId, recoveredEndedAtUtc);
        }

        if (recovered > 0)
        {
            await _progressTrackingService.RecalculateAsync(ct);
        }

        return recovered;
    }

    private static bool IsDanglingSession(CollectionSessionRecord session)
        => string.Equals(session.State, "Prepared", StringComparison.OrdinalIgnoreCase)
           || string.Equals(session.State, "Running", StringComparison.OrdinalIgnoreCase)
           || string.Equals(session.State, "Paused", StringComparison.OrdinalIgnoreCase)
           || !session.EndedAtUtc.HasValue;

    private async Task<DateTimeOffset> ResolveRecoveredEndedAtUtcAsync(CollectionSessionRecord session, IReadOnlyList<AbnormalAnnotationRecord> annotations, CancellationToken ct)
    {
        var rawTs = await TryGetLatestRawSignalTimestampAsync(session.SessionId, ct);
        if (rawTs.HasValue)
        {
            return rawTs.Value;
        }

        var annotationTs = annotations
            .Select(a => a.UpdatedAtUtc >= a.StartedAtUtc ? a.UpdatedAtUtc : a.StartedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        if (annotationTs > DateTimeOffset.MinValue)
        {
            return annotationTs;
        }

        if (session.UpdatedAtUtc > DateTimeOffset.MinValue)
        {
            return session.UpdatedAtUtc;
        }

        return DateTimeOffset.UtcNow;
    }

    private static string? AppendMarker(string? notes, string marker)
    {
        if (!string.IsNullOrWhiteSpace(notes) && notes.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return notes;
        }

        if (string.IsNullOrWhiteSpace(notes))
        {
            return marker;
        }

        return $"{notes} {marker}";
    }

    private static async Task<DateTimeOffset?> TryGetLatestRawSignalTimestampAsync(string sessionId, CancellationToken ct)
    {
        var rawPath = Path.Combine("spool", "raw_signals.jsonl");
        if (!File.Exists(rawPath))
        {
            return null;
        }

        DateTimeOffset? latest = null;
        await using var stream = File.OpenRead(rawPath);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ts_utc", out var tsElement))
                {
                    continue;
                }

                if (!DateTimeOffset.TryParse(tsElement.GetString(), out var ts))
                {
                    continue;
                }

                if (root.TryGetProperty("session_id", out var sidElement))
                {
                    var sid = sidElement.GetString();
                    if (!string.IsNullOrWhiteSpace(sid) && !string.Equals(sid, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                if (!latest.HasValue || ts > latest.Value)
                {
                    latest = ts;
                }
            }
            catch
            {
                // best effort parsing
            }
        }

        return latest;
    }
}
