using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.Configuration;

/// <summary>
/// Configuration options for the Feature Extractor
/// </summary>
public sealed class FeatureExtractorOptions
{
    /// <summary>
    /// Time window size in seconds for feature aggregation
    /// </summary>
    public int WindowSizeSeconds { get; set; } = 60;

    /// <summary>
    /// How often to slide the window and extract features (in seconds)
    /// </summary>
    public int WindowSlideSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of events to buffer in memory per window
    /// </summary>
    public int MaxEventsPerWindow { get; set; } = 1000;

    /// <summary>
    /// Whether to extract features from the signal stream
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether to enable live feature extraction from the broadcast channel.
    /// When false, features are only extracted on-demand (e.g., via Ctrl+E).
    /// </summary>
    public bool EnableLiveExtraction { get; set; } = true;
}
