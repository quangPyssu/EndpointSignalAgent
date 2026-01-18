using System.Threading.Channels;
using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Services;

public sealed class BatchSendService(
    ILogger<BatchSendService> logger,
    Channel<SignalBatchRequest> outgoingQueue,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Sender started");

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
                        await backend.SendAsync(req, stoppingToken);

                        backoff = TimeSpan.FromSeconds(1);
                        break;
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
