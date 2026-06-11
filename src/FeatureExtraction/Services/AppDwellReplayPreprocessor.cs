using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.Services;

internal static class AppDwellReplayPreprocessor
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Injects synthetic AppFocusHeartbeat events into a sorted signal list, one every 15 seconds
    /// per open foreground dwell, derived from ForegroundAppChanged / AppDwell pairs.
    /// Used only in the file replay path — live data already has real heartbeats from the collector.
    /// </summary>
    internal static List<FeatureSignal> InjectHeartbeats(IReadOnlyList<FeatureSignal> signals)
    {
        if (signals.Count == 0)
        {
            return new List<FeatureSignal>();
        }

        var synthetic = new List<FeatureSignal>();

        string? currentAppKey = null;
        string? currentCategory = null;
        string? currentConfidence = null;
        DateTimeOffset currentDwellStart = DateTimeOffset.MinValue;

        foreach (var signal in signals)
        {
            if (signal.Type == SignalEventType.ForegroundAppChanged)
            {
                if (currentAppKey is not null)
                {
                    synthetic.AddRange(GenerateHeartbeats(
                        currentAppKey, currentCategory!, currentConfidence!,
                        currentDwellStart, signal.TimestampUtc));
                }

                currentAppKey = PayloadValueReader.GetString(signal.Payload, "appKey", "unknown");
                currentCategory = PayloadValueReader.GetString(signal.Payload, "category", "Other");
                currentConfidence = PayloadValueReader.GetString(signal.Payload, "confidence", "low");
                currentDwellStart = signal.TimestampUtc;
            }
            else if (signal.Type == SignalEventType.AppDwell && currentAppKey is not null)
            {
                var dwellAppKey = PayloadValueReader.GetString(signal.Payload, "appKey", "");
                if (string.Equals(dwellAppKey, currentAppKey, StringComparison.Ordinal))
                {
                    synthetic.AddRange(GenerateHeartbeats(
                        currentAppKey, currentCategory!, currentConfidence!,
                        currentDwellStart, signal.TimestampUtc));
                    currentAppKey = null;
                }
            }
        }

        // Unclosed dwell at end of file: generate up to (not including) last signal timestamp
        if (currentAppKey is not null)
        {
            synthetic.AddRange(GenerateHeartbeats(
                currentAppKey, currentCategory!, currentConfidence!,
                currentDwellStart, signals[^1].TimestampUtc));
        }

        if (synthetic.Count == 0)
        {
            return new List<FeatureSignal>(signals);
        }

        var result = new List<FeatureSignal>(signals.Count + synthetic.Count);
        result.AddRange(signals);
        result.AddRange(synthetic);
        result.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return result;
    }

    private static IEnumerable<FeatureSignal> GenerateHeartbeats(
        string appKey, string category, string confidence,
        DateTimeOffset dwellStart, DateTimeOffset dwellEnd)
    {
        var t = dwellStart + HeartbeatInterval;
        while (t < dwellEnd)
        {
            yield return new FeatureSignal(
                t,
                SignalEventType.AppFocusHeartbeat,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appKey"] = appKey,
                    ["category"] = category,
                    ["confidence"] = confidence,
                    ["dwellStartUtc"] = dwellStart.ToString("O")
                });
            t += HeartbeatInterval;
        }
    }
}
