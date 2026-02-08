using EndpointSignalAgent.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Services;

/// <summary>
/// Feature Cleanup Service - periodically deletes old sent feature rows
/// to prevent database bloat.
/// </summary>
public sealed class FeatureCleanupService : BackgroundService
{
    private readonly ILogger<FeatureCleanupService> _logger;
    private readonly IFeatureStore _featureStore;

    // Keep sent features for 7 days
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(7);
    
    // Run cleanup daily
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24);

    public FeatureCleanupService(
        ILogger<FeatureCleanupService> logger,
        IFeatureStore featureStore)
    {
        _logger = logger;
        _featureStore = featureStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FeatureCleanupService started (Retention: {Days} days, Interval: {Hours}h)",
            _retentionPeriod.TotalDays, _cleanupInterval.TotalHours);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                try
                {
                    var cutoff = DateTimeOffset.UtcNow - _retentionPeriod;
                    
                    _logger.LogInformation("Running feature cleanup (cutoff: {Cutoff})", cutoff);
                    
                    await _featureStore.DeleteOlderThanAsync(cutoff, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during feature cleanup");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FeatureCleanupService is shutting down");
        }

        _logger.LogInformation("FeatureCleanupService stopped");
    }
}
