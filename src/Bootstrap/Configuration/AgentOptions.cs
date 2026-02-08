namespace EndpointSignalAgent.Bootstrap.Configuration;

public sealed class AgentOptions
{
    public int DefaultReportSeconds { get; set; } = 10;
    public int StatusPollSeconds { get; set; } = 5;

    public int OutgoingQueueCapacity { get; set; } = 300;
    public int DecisionQueueCapacity { get; set; } = 300;
}
