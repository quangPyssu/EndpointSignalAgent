using System.Threading.Channels;
using EndpointSignalAgent.Clients;
using EndpointSignalAgent.Configuration;
using EndpointSignalAgent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointSignalAgent.Services;

public sealed class StatusPollService(
    ILogger<StatusPollService> logger,
    Channel<StatusResponse> decisionQueue,
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
                    // Simulating status poll - actual backend call disabled
                    await Task.Delay(50, stoppingToken); // simulate network delay
                    var status = new StatusResponse(Status: "active"); // simulated response
                    logger.LogDebug("Status poll (simulated) returned: {Status}", status.Status);
                    
                    if (status is not null)
                        await decisionQueue.Writer.WriteAsync(status, stoppingToken);

                    // Actual backend poll (disabled)
                    //var req = new StatusRequest(DeviceId: deviceId);
                    //var status = await backend.PollStatusAsync(req, stoppingToken);
                    //
                    //if (status is not null)
                    //    await decisionQueue.Writer.WriteAsync(status, stoppingToken);
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
