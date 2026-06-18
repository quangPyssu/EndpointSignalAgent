// src/Shared/Services/DecisionProcessorService.cs
using System.Threading.Channels;
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Shared.Services;

public sealed class DecisionProcessorService(
    ILogger<DecisionProcessorService> logger,
    Channel<StatusDecision> decisionQueue,
    IDecisionHandler handler)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Decision processor started");

        try
        {
            await foreach (var decision in decisionQueue.Reader.ReadAllAsync(stoppingToken))
            {
                handler.Handle(decision);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Decision processor crashed");
        }
        finally
        {
            logger.LogInformation("Decision processor stopped");
        }
    }
}
