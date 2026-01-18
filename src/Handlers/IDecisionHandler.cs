using EndpointSignalAgent.Contracts;

namespace EndpointSignalAgent.Handlers;

public interface IDecisionHandler
{
    void Handle(StatusResponse status);
}
