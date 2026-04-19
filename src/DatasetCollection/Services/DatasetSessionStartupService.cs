using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class DatasetSessionStartupService : IHostedService
{
    private readonly AgentOptions _agentOptions;
    private readonly DatasetCollectionOptions _datasetOptions;
    private readonly ICollectionSessionService _sessionService;
    private readonly IProgressTrackingService _progressTrackingService;
    private readonly ILogger<DatasetSessionStartupService> _logger;

    public DatasetSessionStartupService(
        IOptions<AgentOptions> agentOptions,
        IOptions<DatasetCollectionOptions> datasetOptions,
        ICollectionSessionService sessionService,
        IProgressTrackingService progressTrackingService,
        ILogger<DatasetSessionStartupService> logger)
    {
        _agentOptions = agentOptions.Value;
        _datasetOptions = datasetOptions.Value;
        _sessionService = sessionService;
        _progressTrackingService = progressTrackingService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRunForDatasetMode())
        {
            _logger.LogInformation("Dataset startup auto-session is disabled for current mode/configuration.");
            return;
        }

        var recoveredCount = await _sessionService.CloseStaleSessionsAsync("recovered_after_unclean_shutdown=true", "startup-recovery", cancellationToken);
        if (recoveredCount > 0)
        {
            _logger.LogInformation("Recovered {RecoveredCount} stale dataset session(s) before auto-start.", recoveredCount);
        }

        var session = await _sessionService.StartSessionAsync(
            string.Empty,
            normalOnly: false,
            notes: "auto_started=true",
            initiatedBy: "startup",
            cancellationToken);

        await _progressTrackingService.RecalculateAsync(cancellationToken);
        _logger.LogInformation("Dataset session auto-started at host startup: {SessionId}", session.SessionId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!AgentModes.IsDatasetCollection(_agentOptions.Mode) || !_datasetOptions.Enabled)
        {
            return;
        }

        var ended = await _sessionService.EndSessionAsync("auto_ended=true|by=shutdown", "shutdown", cancellationToken);
        if (ended is null)
        {
            return;
        }

        await _progressTrackingService.RecalculateAsync(cancellationToken);
        _logger.LogInformation("Dataset session auto-ended during shutdown: {SessionId}", ended.SessionId);
    }

    private bool ShouldRunForDatasetMode()
        => AgentModes.IsDatasetCollection(_agentOptions.Mode)
           && _datasetOptions.Enabled
           && _datasetOptions.SessionAutoStart;
}
