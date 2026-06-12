using EndpointSignalAgent.FeatureExtraction.SignalAggregator;
using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.Services;

internal static class AppDwellReplayPreprocessor
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // How far back to look for real signals when deciding whether a heartbeat is valid.
    // SystemResourceTick fires every 2s during live collection, so any 60s window during
    // active use will contain real signals. A dead-agent gap contains none.
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Injects synthetic AppFocusHeartbeat events into a sorted signal list, one every 15 seconds
    /// per open foreground dwell, derived from ForegroundAppChanged / AppDwell pairs.
    /// Used only in the file replay path — live data already has real heartbeats from the collector.
    ///
    /// All dwells are liveness-gated: a heartbeat is only emitted at time T if a real signal
    /// exists within the prior 60 seconds. During active use SystemResourceTick fires every 2s,
    /// so liveness always passes. Slots that fall inside a sleep or crash gap are suppressed.
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
                    // Implicit close — no AppDwell was emitted before this switch.
                    // In production, the collector always emits AppDwell before ForegroundAppChanged,
                    // so reaching here means the previous session crashed. Apply liveness gating.
                    synthetic.AddRange(GenerateHeartbeats(
                        currentAppKey, currentCategory!, currentConfidence!,
                        currentDwellStart, signal.TimestampUtc, signals));
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
                        currentDwellStart, signal.TimestampUtc, signals));
                    currentAppKey = null;
                }
            }
        }

        // Unclosed dwell at end of file — apply liveness gating.
        if (currentAppKey is not null)
        {
            synthetic.AddRange(GenerateHeartbeats(
                currentAppKey, currentCategory!, currentConfidence!,
                currentDwellStart, signals[^1].TimestampUtc, signals));
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
        DateTimeOffset dwellStart, DateTimeOffset dwellEnd,
        IReadOnlyList<FeatureSignal> allSignals)
    {
        var t = dwellStart + HeartbeatInterval;
        while (t < dwellEnd)
        {
            if (HasRealSignalInWindow(allSignals, t - LivenessWindow, t))
            {
                yield return MakeHeartbeat(t, appKey, category, confidence, dwellStart);
            }
            t += HeartbeatInterval;
        }
    }

    private static FeatureSignal MakeHeartbeat(
        DateTimeOffset ts, string appKey, string category, string confidence, DateTimeOffset dwellStart) =>
        new(ts, SignalEventType.AppFocusHeartbeat,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appKey"] = appKey,
                ["category"] = category,
                ["confidence"] = confidence,
                ["dwellStartUtc"] = dwellStart.ToString("O")
            });

    private static bool HasRealSignalInWindow(
        IReadOnlyList<FeatureSignal> signals, DateTimeOffset from, DateTimeOffset before)
    {
        // Binary search for first signal >= from
        int lo = 0, hi = signals.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (signals[mid].TimestampUtc < from) lo = mid + 1;
            else hi = mid;
        }
        for (var i = lo; i < signals.Count && signals[i].TimestampUtc < before; i++)
        {
            if (signals[i].Type != SignalEventType.AppFocusHeartbeat)
                return true;
        }
        return false;
    }
}
