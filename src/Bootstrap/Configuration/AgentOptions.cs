namespace EndpointSignalAgent.Bootstrap.Configuration;

public static class AgentModes
{
    public const string Normal = "Normal";
    public const string DatasetCollection = "DatasetCollection";

    public static bool IsValid(string? mode) => string.Equals(mode, Normal, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, DatasetCollection, StringComparison.OrdinalIgnoreCase);

    public static bool IsDatasetCollection(string? mode) => string.Equals(mode, DatasetCollection, StringComparison.OrdinalIgnoreCase);
}

public sealed class AgentOptions
{
    public string Mode { get; set; } = AgentModes.DatasetCollection;
    public int DefaultReportSeconds { get; set; } = 10;
    public int StatusPollSeconds { get; set; } = 5;

    public int OutgoingQueueCapacity { get; set; } = 300;
    public int DecisionQueueCapacity { get; set; } = 300;
}
