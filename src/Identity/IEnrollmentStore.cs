using EndpointSignalAgent.Clients;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public interface IEnrollmentStore
{
    Task<string> GetIdAsync(CancellationToken ct);
}

public sealed class EnrollmentStore : IEnrollmentStore
{
    private readonly BackendClient _backend;
    private readonly ILogger<EnrollmentStore> _logger;
    private readonly string _enrollmentPath = Path.Combine("spool", "enrollment.json");
    private readonly TaskCompletionSource<string> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _started;

    public EnrollmentStore(BackendClient backend, ILogger<EnrollmentStore> logger)
    {
        _backend = backend;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogDebug("Enrollment already started");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Try to load existing enrollment
                var existingId = await LoadEnrollmentAsync();
                if (!string.IsNullOrWhiteSpace(existingId))
                {
                    _logger.LogInformation("Loaded existing enrollment: {DeviceId}", existingId);
                    _tcs.TrySetResult(existingId);
                    return;
                }

                // Enroll new device
                var deviceName = Environment.MachineName;
                _logger.LogInformation("Starting enrollment for device: {DeviceName}", deviceName);

                var retryCount = 0;
                var retryDelay = TimeSpan.FromSeconds(5);
                var maxRetryDelay = TimeSpan.FromSeconds(60);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        retryCount++;
                        _logger.LogDebug("Enrollment attempt #{Attempt}", retryCount);

                        var id = await _backend.EnrollAsync(deviceName, ct);
                        
                        // Save enrollment
                        await SaveEnrollmentAsync(id);
                        
                        _logger.LogInformation("Successfully enrolled with device ID: {DeviceId}", id);
                        _tcs.TrySetResult(id);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("Enrollment cancelled");
                        throw;
                    }
                    catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException socketEx)
                    {
                        _logger.LogWarning("Enrollment attempt #{Attempt} failed: Connection error (SocketError: {SocketError}). " +
                            "Backend may be unreachable. Retrying in {Delay} seconds...", 
                            retryCount, socketEx.SocketErrorCode, retryDelay.TotalSeconds);
                        
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelay.TotalSeconds));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Enrollment attempt #{Attempt} failed, retrying in {Delay} seconds...", 
                            retryCount, retryDelay.TotalSeconds);
                        
                        await Task.Delay(retryDelay, ct);
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 1.5, maxRetryDelay.TotalSeconds));
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

    private async Task<string?> LoadEnrollmentAsync()
    {
        try
        {
            if (!File.Exists(_enrollmentPath))
            {
                _logger.LogDebug("No enrollment file found at {Path}", _enrollmentPath);
                return null;
            }

            var json = await File.ReadAllTextAsync(_enrollmentPath);
            var data = JsonSerializer.Deserialize<EnrollmentData>(json);
            
            if (data is null || string.IsNullOrWhiteSpace(data.DeviceId))
            {
                _logger.LogWarning("Invalid enrollment data in file");
                return null;
            }

            return data.DeviceId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load enrollment file");
            return null;
        }
    }

    private async Task SaveEnrollmentAsync(string deviceId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_enrollmentPath)!);
            
            var data = new EnrollmentData(deviceId, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            
            await File.WriteAllTextAsync(_enrollmentPath, json);
            _logger.LogDebug("Enrollment saved to {Path}", _enrollmentPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save enrollment file (continuing anyway)");
        }
    }

    private sealed record EnrollmentData(string DeviceId, DateTimeOffset EnrolledAt);
}

public sealed class EnrollOnStartupService : BackgroundService
{
    private readonly EnrollmentStore _store;
    private readonly ILogger<EnrollOnStartupService> _logger;

    public EnrollOnStartupService(EnrollmentStore store, ILogger<EnrollOnStartupService> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting enrollment service");
        _store.Start(stoppingToken);
        return Task.CompletedTask;
    }
}
