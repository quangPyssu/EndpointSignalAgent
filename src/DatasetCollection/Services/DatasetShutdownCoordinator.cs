using System.Threading;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class DatasetShutdownCoordinator : IDatasetShutdownCoordinator
{
    private readonly AgentOptions _agentOptions;
    private readonly DatasetCollectionOptions _datasetOptions;
    private readonly ICollectionSessionService _sessionService;
    private readonly IAbnormalTaggingService _abnormalTaggingService;
    private readonly IProgressTrackingService _progressTrackingService;
    private readonly ILogger<DatasetShutdownCoordinator> _logger;
    private int _finalized;

    public DatasetShutdownCoordinator(
        IOptions<AgentOptions> agentOptions,
        IOptions<DatasetCollectionOptions> datasetOptions,
        ICollectionSessionService sessionService,
        IAbnormalTaggingService abnormalTaggingService,
        IProgressTrackingService progressTrackingService,
        ILogger<DatasetShutdownCoordinator> logger)
    {
        _agentOptions = agentOptions.Value;
        _datasetOptions = datasetOptions.Value;
        _sessionService = sessionService;
        _abnormalTaggingService = abnormalTaggingService;
        _progressTrackingService = progressTrackingService;
        _logger = logger;
    }

    public async Task FinalizeAsync(string reason, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _finalized, 1) == 1)
        {
            return;
        }

        if (!AgentModes.IsDatasetCollection(_agentOptions.Mode) || !_datasetOptions.Enabled)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var marker = $"[auto_closed={reason}][closed_at_utc={now:O}]";

            var activeAbnormal = await _abnormalTaggingService.GetActiveAnnotationAsync(ct);
            if (activeAbnormal is not null)
            {
                await _abnormalTaggingService.EndAbnormalSegmentAsync(reason, marker, ct);
                _logger.LogInformation("Closed active abnormal segment during shutdown. Session={SessionId}", activeAbnormal.SessionId);
            }

            var currentSession = _sessionService.CurrentSession;
            if (currentSession is not null
                && (string.Equals(currentSession.State, "Prepared", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(currentSession.State, "Running", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(currentSession.State, "Paused", StringComparison.OrdinalIgnoreCase)
                    || !currentSession.EndedAtUtc.HasValue))
            {
                await _sessionService.EndSessionAsync(marker, reason, ct);
                _logger.LogInformation("Closed active dataset session during shutdown. Session={SessionId}", currentSession.SessionId);
            }

            await _progressTrackingService.RecalculateAsync(ct);
            _logger.LogInformation("Dataset shutdown finalization completed. reason={Reason}", reason);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Dataset shutdown finalization canceled. reason={Reason}", reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dataset shutdown finalization failed. reason={Reason}", reason);
        }
    }
}
