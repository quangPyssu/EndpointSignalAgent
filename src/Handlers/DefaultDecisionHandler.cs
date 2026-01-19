using EndpointSignalAgent.Contracts;
using EndpointSignalAgent.State;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Handlers;

public sealed class DefaultDecisionHandler(
    ILogger<DefaultDecisionHandler> logger,
    IAgentState state) : IDecisionHandler
{
    public void Handle(StatusResponse status)
    {
        logger.LogInformation("Status received: {Status}", status.Status);

        // Parse the status string to make decisions
        // You can extend this to handle different status values
        // e.g., "allow", "deny", "challenge", etc.
    }
}
