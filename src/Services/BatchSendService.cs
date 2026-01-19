using System.Threading.Channels;
using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Services;

public sealed class BatchSendService(
    ILogger<BatchSendService> logger,
    Channel<SignalBatchRequest> outgoingQueue,
    BackendClient backend,
    IEnrollmentStore enrollment)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var id = await enrollment.GetIdAsync(stoppingToken); // waits until enrolled
        logger.LogInformation("Sender started for device {DeviceId}", id);

        var backoff = TimeSpan.FromSeconds(1);
        var backoffMax = TimeSpan.FromSeconds(30);

        try
        {
            await foreach (var req in outgoingQueue.Reader.ReadAllAsync(stoppingToken))
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var response = await backend.SendAsync(req, stoppingToken);
                        
                        if (response?.Success == true)
                        {
                            logger.LogDebug("Signal batch sent successfully");
                            backoff = TimeSpan.FromSeconds(1);
                            break;
                        }
                        else
                        {
                            logger.LogWarning("Signal batch send returned success=false");
                            await Task.Delay(backoff, stoppingToken);
                            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Send failed; retrying in {delay}s", backoff.TotalSeconds);
                        await Task.Delay(backoff, stoppingToken);
                        backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, backoffMax.TotalSeconds));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sender crashed");
        }
        finally
        {
            logger.LogInformation("Sender stopped");
        }
    }
}
