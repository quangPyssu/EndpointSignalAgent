using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.Shared.Handlers;

public interface IDecisionHandler
{
    void Handle(StatusResponse status);
}
