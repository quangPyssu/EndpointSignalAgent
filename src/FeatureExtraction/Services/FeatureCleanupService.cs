using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Feature Cleanup Service - periodically deletes old sent feature rows
/// to prevent database bloat.
/// </summary>
public sealed class FeatureCleanupService : BackgroundService
{
    private const int UnsentRowHardCap = 500;
    private readonly ILogger<FeatureCleanupService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly TimeSpan _retentionPeriod = TimeSpan.FromDays(7);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public FeatureCleanupService(ILogger<FeatureCleanupService> logger, IFeatureStore featureStore)
    {
        _logger = logger;
        _featureStore = featureStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FeatureCleanupService started (cap={Cap}, retention={Days}d, interval={H}h)",
            UnsentRowHardCap, _retentionPeriod.TotalDays, _cleanupInterval.TotalHours);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_cleanupInterval, stoppingToken);
                try
                {
                    var unsentCount = await _featureStore.CountUnsentAsync(stoppingToken);
                    if (unsentCount > UnsentRowHardCap)
                        await _featureStore.PruneUnsentToCapAsync(UnsentRowHardCap, stoppingToken);

                    await _featureStore.DeleteOlderThanAsync(DateTimeOffset.UtcNow - _retentionPeriod, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during feature cleanup");
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogInformation("FeatureCleanupService stopped");
    }
}
