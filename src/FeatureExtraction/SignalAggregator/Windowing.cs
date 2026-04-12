using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

internal readonly record struct FeatureSignal(
    DateTimeOffset TimestampUtc,
    SignalEventType Type,
    IReadOnlyDictionary<string, string> Payload);

internal readonly record struct SlidingWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public long DurationMs => Math.Max(0L, (long)(EndUtc - StartUtc).TotalMilliseconds);
}

internal static class SlidingWindowing
{
    public static DateTimeOffset AlignToStepUtc(DateTimeOffset timestampUtc, int stepSec)
    {
        var stepMs = Math.Max(1, stepSec) * 1000L;
        var unixMs = timestampUtc.ToUniversalTime().ToUnixTimeMilliseconds();
        var aligned = unixMs - (unixMs % stepMs);
        return DateTimeOffset.FromUnixTimeMilliseconds(aligned);
    }

    public static IEnumerable<SlidingWindow> EnumerateWindowStarts(
        DateTimeOffset firstStartUtc,
        DateTimeOffset lastStartUtc,
        int windowSec,
        int stepSec)
    {
        if (lastStartUtc < firstStartUtc)
        {
            yield break;
        }

        var current = firstStartUtc;
        var step = TimeSpan.FromSeconds(stepSec);
        var width = TimeSpan.FromSeconds(windowSec);
        while (current <= lastStartUtc)
        {
            yield return new SlidingWindow(current, current + width);
            current = current + step;
        }
    }

    public static long OverlapMs(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        return Math.Max(0L, (long)(end - start).TotalMilliseconds);
    }

    public static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left <= right ? left : right;
    }

    public static List<(SlidingWindow Window, long OverlapMs, DateTimeOffset OverlapStartUtc, DateTimeOffset OverlapEndUtc)> SplitSegmentAcrossWindows(
        DateTimeOffset segmentStartUtc,
        DateTimeOffset segmentEndUtc,
        IReadOnlyList<SlidingWindow> windows)
    {
        var overlaps = new List<(SlidingWindow Window, long OverlapMs, DateTimeOffset OverlapStartUtc, DateTimeOffset OverlapEndUtc)>();
        if (segmentEndUtc <= segmentStartUtc || windows.Count == 0)
        {
            return overlaps;
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var start = segmentStartUtc > window.StartUtc ? segmentStartUtc : window.StartUtc;
            var end = segmentEndUtc < window.EndUtc ? segmentEndUtc : window.EndUtc;
            var overlap = Math.Max(0L, (long)(end - start).TotalMilliseconds);
            if (overlap > 0)
            {
                overlaps.Add((window, overlap, start, end));
            }
        }

        return overlaps;
    }
}

internal static class PayloadValueReader
{
    public static string GetString(IReadOnlyDictionary<string, string> payload, string key, string fallback = "")
    {
        if (payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return fallback;
    }

    public static bool TryGetInt(IReadOnlyDictionary<string, string> payload, string key, out int value)
    {
        value = 0;
        if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return int.TryParse(raw, out value);
    }

    public static bool TryGetLong(IReadOnlyDictionary<string, string> payload, string key, out long value)
    {
        value = 0;
        if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return long.TryParse(raw, out value);
    }

    public static bool TryGetBool(IReadOnlyDictionary<string, string> payload, string key, out bool value)
    {
        value = false;
        if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (bool.TryParse(text, out value))
        {
            return true;
        }

        if (string.Equals(text, "1", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        return false;
    }

    public static bool IsTruthy(string value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static double GetDouble(IReadOnlyDictionary<string, string> payload, string key, double fallback = 0.0)
    {
        if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }
}

internal static class FeatureMath
{
    public static double SafeDivide(double numerator, double denominator)
    {
        if (denominator <= 0.0)
        {
            return 0.0;
        }

        return numerator / denominator;
    }

    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count <= 1)
        {
            return 0.0;
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return Math.Sqrt(variance);
    }

    public static double EntropyFromShares(IEnumerable<double> shares)
    {
        var entropy = 0.0;
        foreach (var share in shares)
        {
            if (share <= 0)
            {
                continue;
            }

            entropy -= share * Math.Log(share);
        }

        return entropy;
    }
}
