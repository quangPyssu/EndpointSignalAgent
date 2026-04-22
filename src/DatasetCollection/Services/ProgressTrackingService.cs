using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using EndpointSignalAgent.DatasetCollection.Contracts;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class ProgressTrackingService : BackgroundService, IProgressTrackingService
{
    private readonly DatasetCollectionOptions _options;
    private readonly ICollectionSessionService _sessionService;
    private readonly ICollectionManifestService _manifestService;
    private readonly ICollectionControl _collectionControl;

    public ProgressTrackingService(
        IOptions<DatasetCollectionOptions> options,
        ICollectionSessionService sessionService,
        ICollectionManifestService manifestService,
        ICollectionControl collectionControl)
    {
        _options = options.Value;
        _sessionService = sessionService;
        _manifestService = manifestService;
        _collectionControl = collectionControl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableProgressTracking)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RecalculateAsync(stoppingToken);
        }
    }

    public async Task<ProgressStateRecord> RecalculateAsync(CancellationToken ct)
    {
        var sessions = await _sessionService.GetSessionsAsync(ct);
        var completed = sessions.Where(s => string.Equals(s.State, "Completed", StringComparison.OrdinalIgnoreCase)).ToList();
        var validCollectionDays = completed
            .Where(s => s.StartedAtUtc.HasValue && s.EndedAtUtc.HasValue && (s.EndedAtUtc.Value - s.StartedAtUtc.Value).TotalMinutes >= _options.MinSessionMinutes)
            .Select(s => s.StartedAtUtc!.Value.Date)
            .Distinct()
            .Count();

        var runtimeHours = completed.Sum(s => s.StartedAtUtc.HasValue && s.EndedAtUtc.HasValue ? (s.EndedAtUtc.Value - s.StartedAtUtc.Value).TotalHours : 0d);
        var activeHours = Math.Max(0, runtimeHours * 0.85d - (_collectionControl.IsPaused ? 0.1d : 0d));

        var allAnnotations = new List<AbnormalAnnotationRecord>();
        foreach (var session in sessions)
        {
            allAnnotations.AddRange(await _manifestService.LoadAnnotationsAsync(session.SessionId, ct));
        }

        var abnormalMinutes = allAnnotations
            .Where(a => a.EndedAtUtc.HasValue)
            .Sum(a => (a.EndedAtUtc!.Value - a.StartedAtUtc).TotalMinutes);

        var abnormalScenarios = allAnnotations
            .Where(a => a.IsComplete)
            .Select(a => a.ScenarioCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var earliest = sessions.MinBy(s => s.StartedAtUtc ?? DateTimeOffset.UtcNow)?.StartedAtUtc ?? DateTimeOffset.UtcNow;
        var studySpanWeeks = Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - earliest).TotalDays / 7d));

        var ratioParts = new[]
        {
            ThresholdRatio(studySpanWeeks, _options.StudyWeekTarget),
            ThresholdRatio(validCollectionDays, _options.WeeklyActiveDayTarget * _options.StudyWeekTarget),
            ThresholdRatio(completed.Count, _options.ExpectedSessionCount),
            ThresholdRatio(activeHours, _options.DailyActiveHourTarget * _options.WeeklyActiveDayTarget * _options.StudyWeekTarget),
            ThresholdRatio(abnormalScenarios, _options.ExpectedAbnormalScenarioCount),
            ThresholdRatio(abnormalMinutes, _options.ExpectedAbnormalMinutesMin)
        };

        var completionRatio = ClampRatio(ratioParts.Average());
        var progress = new ProgressStateRecord(
            StudySpanWeeks: studySpanWeeks,
            ValidCollectionDays: validCollectionDays,
            TotalSessionsCompleted: completed.Count,
            TotalRuntimeHours: runtimeHours,
            TotalActiveHours: activeHours,
            AbnormalScenariosCompleted: abnormalScenarios,
            AbnormalMinutes: abnormalMinutes,
            CoreSignalCoverageDaysOk: validCollectionDays,
            CompletionStatus: completionRatio >= 1 ? "Complete" : "In progress",
            CompletionRatio: completionRatio,
            LastUpdatedUtc: DateTimeOffset.UtcNow);

        await _manifestService.SaveProgressAsync(progress, ct);
        return progress;
    }

    public async Task<ProgressStateRecord> GetCurrentAsync(CancellationToken ct)
        => await _manifestService.LoadProgressAsync(ct) ?? await RecalculateAsync(ct);

    public async Task<ProgressTraySnapshot> GetTraySnapshotAsync(CancellationToken ct)
    {
        var progress = await GetCurrentAsync(ct);
        return new ProgressTraySnapshot(
            TotalRuntimeHours: progress.TotalRuntimeHours,
            TotalActiveHours: progress.TotalActiveHours,
            TotalSessionsCompleted: progress.TotalSessionsCompleted,
            ValidCollectionDays: progress.ValidCollectionDays,
            CompletionRatio: ClampRatio(progress.CompletionRatio),
            CompletionStatus: progress.CompletionStatus);
    }

    private static double ThresholdRatio(double actual, double target)
    {
        if (target <= 0)
        {
            return 1;
        }

        return Math.Min(1, actual / target);
    }

    private static double ClampRatio(double ratio) => Math.Clamp(ratio, 0, 1);
}
