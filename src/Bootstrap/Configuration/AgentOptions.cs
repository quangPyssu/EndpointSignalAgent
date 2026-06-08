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

    public DeviceGuardOptions DeviceGuard { get; set; } = new();

    public sealed class DeviceGuardOptions
    {
        /// <summary>Enable idle-based lock/sleep enforcement.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Lock workstation after this many idle seconds. 0 = disabled.</summary>
        public int IdleLockThresholdSec { get; set; } = 3600;

        /// <summary>Sleep device after this many idle seconds. 0 = disabled.</summary>
        public int IdleSleepThresholdSec { get; set; } = 0;

        /// <summary>How often to check idle state (seconds).</summary>
        public int PollIntervalSec { get; set; } = 30;
    }
}
