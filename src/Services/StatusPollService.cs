using System.Threading.Channels;
using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Services;

public sealed class StatusPollService(
    ILogger<StatusPollService> logger,
    Channel<StatusResponse> decisionQueue,
    IOptions<AgentOptions> agentOptions,
    IAgentIdentity identity,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Status poller started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var req = new StatusRequest(identity.DeviceId, identity.SessionId);
                    var status = await backend.PollStatusAsync(req, stoppingToken);

                    if (status is not null)
                        await decisionQueue.Writer.WriteAsync(status, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Status poll failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(agentOptions.Value.StatusPollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            decisionQueue.Writer.TryComplete();
            logger.LogInformation("Status poller stopped");
        }
    }
}
