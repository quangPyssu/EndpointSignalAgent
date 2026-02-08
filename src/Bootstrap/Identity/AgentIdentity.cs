namespace EndpointSignalAgent.Bootstrap.Identity;

public interface IAgentIdentity
{
    string DeviceId { get; }
    string SessionId { get; }
}

public sealed class AgentIdentity : IAgentIdentity
{
    public string DeviceId { get; } = Environment.MachineName;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
}
