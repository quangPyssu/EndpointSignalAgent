using EndpointSignalAgent.Bootstrap.Backend;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EndpointSignalAgent.Bootstrap.Identity;

public interface IEnrollmentStore
{
    Task<string> GetIdAsync(CancellationToken ct);
}

public sealed class EnrollmentStore : IEnrollmentStore
{
    private readonly BackendClient _backend;
    private readonly DeviceTokenStore _tokenStore;
    private readonly ILogger<EnrollmentStore> _logger;
    private readonly string _enrollmentPath = Path.Combine("spool", "enrollment.json");
    private readonly TaskCompletionSource<string> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Action<int>? _onReportSecondsReceived;
    private int _started;

    public EnrollmentStore(
        BackendClient backend,
        DeviceTokenStore tokenStore,
        ILogger<EnrollmentStore> logger,
        Action<int>? onReportSecondsReceived = null)
    {
        _backend = backend;
        _tokenStore = tokenStore;
        _logger = logger;
        _onReportSecondsReceived = onReportSecondsReceived;
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var existing = await LoadEnrollmentAsync();
                if (existing is not null)
                {
                    _logger.LogInformation("Loaded existing enrollment: {DeviceId}", existing.DeviceId);
                    _tokenStore.Set(existing.Token);
                    _onReportSecondsReceived?.Invoke(existing.ReportSeconds);
                    _tcs.TrySetResult(existing.DeviceId);
                    return;
                }

                var retryDelay = TimeSpan.FromSeconds(5);
                const double maxRetryDelaySec = 60.0;
                var attempt = 0;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        attempt++;
                        _logger.LogDebug("Enrollment attempt #{Attempt}", attempt);

                        // Wait for backend to be ready before first enroll attempt
                        if (attempt == 1)
                        {
                            while (!ct.IsCancellationRequested && !await _backend.CheckReadyAsync(ct))
                            {
                                _logger.LogDebug("Backend not ready yet, waiting 5s before enroll...");
                                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                            }
                        }

                        var resp = await _backend.EnrollAsync(ct);

                        await SaveEnrollmentAsync(resp.DeviceId, resp.Token, resp.ReportSeconds);
                        _tokenStore.Set(resp.Token);
                        _tcs.TrySetResult(resp.DeviceId);
                        _onReportSecondsReceived?.Invoke(resp.ReportSeconds);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx)
                    {
                        _logger.LogWarning(
                            "Enrollment attempt #{Attempt} failed (SocketError: {SocketError}). Retrying in {Delay}s",
                            attempt, socketEx.SocketErrorCode, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelaySec));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Enrollment attempt #{Attempt} failed, retrying in {Delay}s",
                            attempt, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelaySec));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Enrollment process cancelled");
                _tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in enrollment process");
                _tcs.TrySetException(ex);
            }
        }, ct);
    }

    public Task<string> GetIdAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

    private async Task<EnrollmentData?> LoadEnrollmentAsync()
    {
        try
        {
            if (!File.Exists(_enrollmentPath)) return null;

            var json = await File.ReadAllTextAsync(_enrollmentPath);
            var data = JsonSerializer.Deserialize<EnrollmentData>(json);

            if (data is null || string.IsNullOrWhiteSpace(data.DeviceId) || string.IsNullOrWhiteSpace(data.Token))
            {
                _logger.LogWarning("Invalid or legacy enrollment file — re-enrolling");
                return null;
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enrollment file");
            return null;
        }
    }

    private async Task SaveEnrollmentAsync(string deviceId, string token, int reportSeconds)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_enrollmentPath)!);
            var data = new EnrollmentData(deviceId, token, reportSeconds, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_enrollmentPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save enrollment file (continuing anyway)");
        }
    }

    private sealed record EnrollmentData(string DeviceId, string Token, int ReportSeconds, DateTimeOffset EnrolledAt);
}

public sealed class EnrollOnStartupService(
    EnrollmentStore store,
    ILogger<EnrollOnStartupService> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting enrollment service");
        store.Start(stoppingToken);
        return Task.CompletedTask;
    }
}
