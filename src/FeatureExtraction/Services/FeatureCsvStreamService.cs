// src/FeatureExtraction/Services/FeatureCsvStreamService.cs
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Storage;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Polls the local FeatureStore for unsent rows and POSTs them in JSON batches
/// to POST /features. SQLite acts as the write-ahead buffer so no data is lost
/// during disconnects.
/// </summary>
public sealed class FeatureCsvStreamService : BackgroundService
{
    private readonly ILogger<FeatureCsvStreamService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _extractorOptions;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly BackendClient _backend;

    public FeatureCsvStreamService(
        ILogger<FeatureCsvStreamService> logger,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> extractorOptions,
        IOptions<BackendOptions> backendOptions,
        BackendClient backend)
    {
        _logger = logger;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _extractorOptions = extractorOptions;
        _backendOptions = backendOptions;
        _backend = backend;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_extractorOptions.Value.Enabled)
        {
            _logger.LogInformation("FeatureCsvStreamService disabled (FeatureExtractor.Enabled=false)");
            return;
        }

        var deviceId = await _enrollment.GetIdAsync(stoppingToken);
        _logger.LogInformation("FeatureCsvStreamService started for device {DeviceId}", deviceId);

        var pollInterval = TimeSpan.FromSeconds(30);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMaxSec = 120.0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(pollInterval, stoppingToken);

                try
                {
                    var unsent = await _featureStore.GetUnsentAsync(limit: 100, stoppingToken);
                    if (unsent.Count == 0)
                    {
                        backoff = TimeSpan.FromSeconds(5);
                        continue;
                    }

                    if (!_backendOptions.Value.UseBackend)
                    {
                        var ids = unsent.Select(r => r.Id).ToList();
                        await _featureStore.MarkAsSentAsync(ids, stoppingToken);
                        backoff = TimeSpan.FromSeconds(5);
                        continue;
                    }

                    // Group by (feature_version, window_sec) — backend requires a single value per request
                    var groups = unsent
                        .GroupBy(r => (r.FeatureVersion, r.WindowSec))
                        .ToList();

                    var sentIds = new List<long>();
                    var anyFailed = false;

                    foreach (var group in groups)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var rows = group
                            .Select(r =>
                            {
                                var row = new Dictionary<string, object>
                                {
                                    ["window_start_ts"] = r.WindowStartTs.ToUnixTimeSeconds()
                                };
                                foreach (var (k, v) in r.Features)
                                    row[k] = v;
                                return row;
                            })
                            .ToList();

                        var req = new FeaturesRequest(
                            DeviceId: deviceId,
                            FeatureVersion: group.Key.FeatureVersion,
                            WindowSec: group.Key.WindowSec,
                            Rows: rows);

                        try
                        {
                            var resp = await _backend.PostFeaturesAsync(req, stoppingToken);
                            _logger.LogDebug("Features batch accepted={Accepted}", resp?.Accepted ?? 0);
                            sentIds.AddRange(group.Select(r => r.Id));
                        }
                        catch (HttpRequestException ex)
                        {
                            _logger.LogWarning(ex, "Features batch failed for version={Version}", group.Key.FeatureVersion);
                            anyFailed = true;
                            break;
                        }
                    }

                    if (sentIds.Count > 0)
                        await _featureStore.MarkAsSentAsync(sentIds, stoppingToken);

                    if (anyFailed)
                    {
                        _logger.LogWarning("Features upload partial failure; retrying in {Backoff}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec));
                    }
                    else
                    {
                        backoff = TimeSpan.FromSeconds(5);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FeatureCsvStreamService cycle error");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec));
                }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("FeatureCsvStreamService stopped");
    }
}
