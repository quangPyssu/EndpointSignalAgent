using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.DatasetCollection.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace EndpointSignalAgent.DatasetCollection.Services;

public sealed class DatasetShutdownHooksService : IHostedService
{
    private readonly AgentOptions _agentOptions;
    private readonly DatasetCollectionOptions _datasetOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IDatasetShutdownCoordinator _shutdownCoordinator;
    private readonly ILogger<DatasetShutdownHooksService> _logger;

    public DatasetShutdownHooksService(
        IOptions<AgentOptions> agentOptions,
        IOptions<DatasetCollectionOptions> datasetOptions,
        IHostApplicationLifetime lifetime,
        IDatasetShutdownCoordinator shutdownCoordinator,
        ILogger<DatasetShutdownHooksService> logger)
    {
        _agentOptions = agentOptions.Value;
        _datasetOptions = datasetOptions.Value;
        _lifetime = lifetime;
        _shutdownCoordinator = shutdownCoordinator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!AgentModes.IsDatasetCollection(_agentOptions.Mode) || !_datasetOptions.Enabled)
        {
            return Task.CompletedTask;
        }

        _lifetime.ApplicationStopping.Register(() => _ = BestEffortFinalizeAsync("host_shutdown"));
        SystemEvents.SessionEnding += OnSessionEnding;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        return Task.CompletedTask;
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
        => _ = BestEffortFinalizeAsync("session_ending");

    private void OnProcessExit(object? sender, EventArgs e)
        => _ = BestEffortFinalizeAsync("process_exit");

    private async Task BestEffortFinalizeAsync(string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _shutdownCoordinator.FinalizeAsync(reason, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort dataset shutdown hook failed. reason={Reason}", reason);
        }
    }
}
