using System.Net.Http.Headers;
using System.Text;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.FeatureExtraction.Configuration;
using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Polls the local FeatureStore for unsent rows and POSTs each as a single
/// text/csv row to the backend. Rows are sent serially, oldest first.
/// SQLite acts as the write-ahead buffer so no data is lost during disconnects.
/// </summary>
public sealed class FeatureCsvStreamService : BackgroundService
{
    private readonly ILogger<FeatureCsvStreamService> _logger;
    private readonly IFeatureStore _featureStore;
    private readonly IEnrollmentStore _enrollment;
    private readonly IOptions<FeatureExtractorOptions> _extractorOptions;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly IHttpClientFactory _httpClientFactory;

    public FeatureCsvStreamService(
        ILogger<FeatureCsvStreamService> logger,
        IFeatureStore featureStore,
        IEnrollmentStore enrollment,
        IOptions<FeatureExtractorOptions> extractorOptions,
        IOptions<BackendOptions> backendOptions,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _featureStore = featureStore;
        _enrollment = enrollment;
        _extractorOptions = extractorOptions;
        _backendOptions = backendOptions;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_extractorOptions.Value.Enabled)
        {
            _logger.LogInformation("FeatureCsvStreamService disabled (FeatureExtractor.Enabled=false)");
            return;
        }

        await _enrollment.GetIdAsync(stoppingToken); // wait for enrollment

        _logger.LogInformation("FeatureCsvStreamService started");

        var pollInterval = TimeSpan.FromSeconds(30);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMax = 120.0;

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

                    var anyFailed = false;
                    foreach (var row in unsent)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var sent = _backendOptions.Value.UseBackend
                            ? await PostRowAsync(row, stoppingToken)
                            : true; // offline mode: mark sent immediately

                        if (sent)
                        {
                            await _featureStore.MarkAsSentAsync([row.Id], stoppingToken);
                        }
                        else
                        {
                            anyFailed = true;
                            break; // stop this batch; retry next poll cycle
                        }
                    }

                    if (!anyFailed)
                        backoff = TimeSpan.FromSeconds(5);
                    else
                    {
                        _logger.LogWarning("Row upload failed; retrying in {Backoff}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FeatureCsvStreamService cycle error");
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax));
                }
            }
        }
        catch (OperationCanceledException) { }

        _logger.LogInformation("FeatureCsvStreamService stopped");
    }

    private async Task<bool> PostRowAsync(
        EndpointSignalAgent.FeatureExtraction.Contracts.FeatureRow row,
        CancellationToken ct)
    {
        try
        {
            var csvBody = FeatureCsvRowSerializer.Header + "\n" + FeatureCsvRowSerializer.SerializeRow(row);
            using var content = new StringContent(csvBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

            using var client = _httpClientFactory.CreateClient("BackendClient");
            using var response = await client.PostAsync(
                _backendOptions.Value.FeatureRowCsvPath, content, ct);

            if (response.IsSuccessStatusCode) return true;

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("CSV row POST failed ({Status}): {Body}", response.StatusCode, body);
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "CSV row POST — HTTP error");
            return false;
        }
    }
}
