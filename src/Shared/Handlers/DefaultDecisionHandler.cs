using EndpointSignalAgent.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Shared.Handlers;

public sealed class DefaultDecisionHandler(ILogger<DefaultDecisionHandler> logger) : IDecisionHandler
{
    public void Handle(StatusResponse status)
    {
        logger.LogInformation("Status received: {Status}", status.Status);
    }
}
