// src/Shared/Handlers/DefaultDecisionHandler.cs
using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.Shared.State;
using EndpointSignalAgent.Shared.Utilities;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.Shared.Handlers;

public sealed class DefaultDecisionHandler(
    ILogger<DefaultDecisionHandler> logger,
    IAgentState agentState) : IDecisionHandler
{
    public void Handle(StatusDecision decision)
    {
        logger.LogInformation(
            "Decision received: label={Label} score={Score} reason={Reason}",
            decision.Label, decision.Score, decision.Reason);

        DispatchLabel(
            label: decision.Label,
            state: agentState,
            decision: decision,
            lockWorkstation: WindowsActions.LockWorkstation,
            forceSleep: WindowsActions.ForceSleep);
    }

    internal static void DispatchLabel(
        string label,
        IAgentState state,
        StatusDecision decision,
        Action lockWorkstation,
        Action forceSleep)
    {
        state.SetDecision(decision);

        switch (label.ToLowerInvariant())
        {
            case KnownLabels.Lock:
                lockWorkstation();
                break;
            case KnownLabels.ForceSleep:
                forceSleep();
                break;
            case KnownLabels.Allow:
            case KnownLabels.Deny:
            case KnownLabels.Unknown:
            default:
                break;
        }
    }
}
