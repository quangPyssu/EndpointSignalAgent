// src/Shared/Handlers/IDecisionHandler.cs
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.Shared.Handlers;

public interface IDecisionHandler
{
    void Handle(StatusDecision decision);
}
