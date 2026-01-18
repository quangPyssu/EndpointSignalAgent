using System.Threading.Channels;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Identity;
using EndpointSignalAgent.Providers;
using EndpointSignalAgent.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Services;

public sealed class BatchProducerService(
    ILogger<BatchProducerService> logger,
    Channel<SignalBatchRequest> outgoingQueue,
    IOptions<AgentOptions> agentOptions,
    IAgentIdentity identity,
    IAgentState state,
    IEnumerable<ISignalProvider> signalProviders)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Producer started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var events = new List<SignalEvent>(capacity: 8);

                foreach (var p in signalProviders)
                {
                    var batch = await p.CollectAsync(stoppingToken);
                    if (batch.Count > 0) events.AddRange(batch);
                }

                var req = new SignalBatchRequest(
                    DeviceId: identity.DeviceId,
                    SessionId: identity.SessionId,
                    SentAt: DateTimeOffset.UtcNow,
                    Events: events);

                await outgoingQueue.Writer.WriteAsync(req, stoppingToken);

                var interval = state.GetReportSecondsOrDefault(agentOptions.Value.DefaultReportSeconds);
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Producer crashed");
        }
        finally
        {
            outgoingQueue.Writer.TryComplete();
            logger.LogInformation("Producer stopped");
        }
    }
}
