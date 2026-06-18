// src/Shared/Services/StatusPollService.cs
using System.Threading.Channels;
using EndpointSignalAgent.Bootstrap.Backend;
using EndpointSignalAgent.Bootstrap.Configuration;
using EndpointSignalAgent.Bootstrap.Identity;
using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Shared.Services;

public sealed class StatusPollService(
    ILogger<StatusPollService> logger,
    Channel<StatusDecision> decisionQueue,
    IOptions<AgentOptions> agentOptions,
    IEnrollmentStore enrollment,
    BackendClient backend)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var deviceId = await enrollment.GetIdAsync(stoppingToken);
        logger.LogInformation("Status poller started for device {DeviceId}", deviceId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var decision = await backend.PollStatusAsync(deviceId, stoppingToken);
                    if (decision is not null)
                        await decisionQueue.Writer.WriteAsync(decision, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Status poll failed");
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(agentOptions.Value.StatusPollSeconds), stoppingToken);
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
