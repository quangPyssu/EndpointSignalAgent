using System.Text;
using System.Text.Json;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Identity;
using EndpointSignalAgent.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Services;

/// <summary>
/// Feature Upload Service - periodically checks for unsent feature rows
/// and uploads them to the backend.
/// </summary>
public sealed class FeatureUploadService : BackgroundService
{
    private readonly ILogger<FeatureUploadService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _options;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly IHttpClientFactory _httpClientFactory;

    public FeatureUploadService(
        ILogger<FeatureUploadService> logger,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> options,
        IOptions<BackendOptions> backendOptions,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _options = options;
        _backendOptions = backendOptions;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("FeatureUploadService is disabled (FeatureExtractor.Enabled = false)");
            return;
        }

        // Wait for device enrollment
        var deviceId = await _enrollment.GetIdAsync(stoppingToken);
        
        _logger.LogInformation("FeatureUploadService started for device {DeviceId}", deviceId);

        var uploadInterval = TimeSpan.FromSeconds(120); // Upload every 2 minutes
        var backoff = TimeSpan.FromSeconds(5);
        var backoffMax = TimeSpan.FromSeconds(60);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(uploadInterval, stoppingToken);

                try
                {
                    // Get unsent feature rows (batch up to 50)
                    var unsentRows = await _featureStore.GetUnsentAsync(limit: 50, stoppingToken);

                    if (unsentRows.Count == 0)
                    {
                        _logger.LogDebug("No unsent feature rows to upload");
                        backoff = TimeSpan.FromSeconds(5); // Reset backoff
                        continue;
                    }

                    _logger.LogInformation("Uploading {Count} unsent feature rows", unsentRows.Count);

                    // Send to backend
                    var success = await SendFeatureBatchAsync(deviceId, unsentRows, stoppingToken);

                    if (success)
                    {
                        // Mark as sent
                        var ids = unsentRows.Select(r => r.Id).ToList();
                        await _featureStore.MarkAsSentAsync(ids, stoppingToken);

                        _logger.LogInformation("Successfully uploaded and marked {Count} feature rows as sent", ids.Count);
                        backoff = TimeSpan.FromSeconds(5); // Reset backoff
                    }
                    else
                    {
                        _logger.LogWarning("Failed to upload feature batch, will retry in {Backoff}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during feature upload cycle");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("FeatureUploadService is shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FeatureUploadService crashed");
        }

        _logger.LogInformation("FeatureUploadService stopped");
    }

    private async Task<bool> SendFeatureBatchAsync(
        string deviceId, 
        List<FeatureRow> rows, 
        CancellationToken ct)
    {
        try
        {
            // Convert to DTOs (exclude internal fields)
            var dtos = rows.Select(r => new FeatureRowDto(
                WindowSec: r.WindowSec,
                WindowStartTs: r.WindowStartTs,
                FeatureVersion: r.FeatureVersion,
                Features: r.Features
            )).ToList();

            var request = new FeatureBatchRequest(
                DeviceId: deviceId,
                Features: dtos
            );

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var httpClient = _httpClientFactory.CreateClient("BackendClient");
            
            // Use configured features endpoint
            var endpoint = _backendOptions.Value.FeaturesPath;
            
            using var response = await httpClient.PostAsync(endpoint, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Feature batch uploaded successfully (Status: {Status})", response.StatusCode);
                return true;
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Feature batch upload failed (Status: {Status}, Body: {Body})", 
                    response.StatusCode, responseBody);
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error during feature batch upload");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during feature batch upload");
            return false;
        }
    }
}
