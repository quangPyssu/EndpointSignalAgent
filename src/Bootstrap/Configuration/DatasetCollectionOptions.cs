namespace EndpointSignalAgent.Bootstrap.Configuration;

public sealed class DatasetCollectionOptions
{
    public const string SectionName = "DatasetCollection";

    public bool Enabled { get; set; } = true;
    public string ParticipantId { get; set; } = "P001";
    public string StudyId { get; set; } = "thesis-mature-dataset-v1";
    public string ProtocolVersion { get; set; } = "1.0";
    public bool SessionAutoStart { get; set; }
    public bool RequireSessionMetadata { get; set; } = true;
    public bool EnableAbnormalTagging { get; set; } = true;
    public bool EnableProgressTracking { get; set; } = true;
    public double DailyActiveHourTarget { get; set; } = 4.0;
    public int WeeklyActiveDayTarget { get; set; } = 5;
    public int StudyWeekTarget { get; set; } = 4;
    public int MinSessionMinutes { get; set; } = 20;
    public int ExpectedSessionCount { get; set; } = 16;
    public int ExpectedAbnormalScenarioCount { get; set; } = 6;
    public int ExpectedAbnormalMinutesMin { get; set; } = 60;
    public string ExportRoot { get; set; } = "exports";
    public string ManifestRoot { get; set; } = "spool/manifests";
}
