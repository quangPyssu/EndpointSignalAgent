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
        logger.LogInformation("Status decision={Decision} risk={Risk} msg={Msg}",
            status.Decision, status.RiskScore, status.Message);

        if (status.NextReportSeconds is { } s)
            state.TrySetReportSeconds(s);
    }
}
