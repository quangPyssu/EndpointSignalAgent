// src/SignalCollection/Services/SignalUploadService.cs
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.SignalCollection.Services;

public sealed class SignalUploadService(
    ILogger<SignalUploadService> logger,
    IEnrollmentStore enrollment,
    ISignalProvider signalProvider,
    BackendClient backend,
    IOptions<BackendOptions> backendOptions)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = backendOptions.Value;
        if (!opts.UseBackend)
        {
            logger.LogInformation("SignalUploadService disabled (Backend:UseBackend=false)");
            return;
        }

        var deviceId = await enrollment.GetIdAsync(stoppingToken);
        logger.LogInformation("SignalUploadService started for device {DeviceId}", deviceId);

        var interval = TimeSpan.FromSeconds(opts.SignalUploadIntervalSeconds);
        var backoff = TimeSpan.FromSeconds(5);
        const double backoffMaxSec = 120.0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken);

                try
                {
                    var signals = await signalProvider.CollectAsync(stoppingToken);
                    if (signals.Count == 0) continue;

                    var batched = signals.Count <= opts.SignalUploadBatchSize
                        ? (IReadOnlyList<SignalEvent>)signals
                        : signals.Take(opts.SignalUploadBatchSize).ToList();

                    var req = new SignalBatchRequest(DeviceId: deviceId, Signals: batched);
                    var resp = await backend.SendAsync(req, stoppingToken);

                    logger.LogDebug("Uploaded {Count} signals, accepted={Accepted}",
                        batched.Count, resp?.Accepted ?? 0);

                    backoff = TimeSpan.FromSeconds(5);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Signal upload failed; retrying in {Backoff}s", backoff.TotalSeconds);
                    await Task.Delay(backoff, stoppingToken);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMaxSec));
                }
            }
        }
        catch (OperationCanceledException) { }

        logger.LogInformation("SignalUploadService stopped");
    }
}
