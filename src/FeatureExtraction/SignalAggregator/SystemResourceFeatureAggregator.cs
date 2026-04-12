using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Resource features sourced from SystemResourceCollector signals.
/// </summary>
internal sealed class SystemResourceFeatureAggregator
{
    public SystemResourceFeatureResult ExtractFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var features = FeatureSchema.SystemColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var inWindow = events
            .Where(e => e.Type == SignalEventType.SystemResourceSample)
            .Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        if (inWindow.Count == 0)
        {
            return new SystemResourceFeatureResult(features);
        }

        var cpuMean = Values(inWindow, "cpu_mean_pct");
        var memMean = Values(inWindow, "mem_mean_used_pct");
        var gpuMean = Values(inWindow, "gpu_mean_pct");
        var gpuMem = Values(inWindow, "gpu_mem_used_pct");
        var netTx = Values(inWindow, "net_tx_mean_kbps");
        var netRx = Values(inWindow, "net_rx_mean_kbps");
        var netTotal = netTx.Zip(netRx, (tx, rx) => tx + rx).ToArray();

        features["cpu_usage_mean"] = Mean(cpuMean);
        features["cpu_usage_max"] = Max(cpuMean);
        features["cpu_usage_std"] = StdDev(cpuMean);
        features["cpu_usage_high_ratio"] = Ratio(cpuMean, x => x > 80d);
        features["cpu_spike_count"] = CountSpikes(cpuMean, 20d);

        features["ram_usage_mean"] = Mean(memMean);
        features["ram_usage_max"] = Max(memMean);
        features["ram_usage_std"] = StdDev(memMean);
        features["ram_high_usage_ratio"] = Ratio(memMean, x => x > 85d);
        features["ram_pressure_events"] = CountThresholdCrossings(memMean, 85d);

        features["gpu_available"] = gpuMean.Any(x => x > 0d) || gpuMem.Any(x => x > 0d) ? 1d : 0d;
        features["gpu_usage_mean"] = Mean(gpuMean);
        features["gpu_usage_max"] = Max(gpuMean);
        features["gpu_usage_std"] = StdDev(gpuMean);
        features["gpu_memory_usage_mean"] = Mean(gpuMem);
        features["gpu_high_usage_ratio"] = Ratio(gpuMean, x => x > 70d);

        features["net_bytes_sent_mean"] = Mean(netTx);
        features["net_bytes_recv_mean"] = Mean(netRx);
        features["net_bytes_total_mean"] = Mean(netTotal);
        features["net_bytes_total_max"] = Max(netTotal);
        features["net_activity_ratio"] = Ratio(netTotal, x => x > 1d);
        features["net_throughput_std"] = StdDev(netTotal);
        features["net_spike_count"] = CountSpikes(netTotal, 1000d);

        features["system_load_index"] =
            (features["cpu_usage_mean"] * 0.4d) +
            (features["ram_usage_mean"] * 0.35d) +
            (features["gpu_usage_mean"] * 0.25d);

        features["resource_variability_index"] = Mean(new[]
        {
            features["cpu_usage_std"],
            features["ram_usage_std"],
            features["gpu_usage_std"],
            features["net_throughput_std"]
        });

        features["cpu_ram_correlation_proxy"] = Math.Abs(features["cpu_usage_mean"] - features["ram_usage_mean"]);
        features["active_resource_ratio"] = Mean(new[]
        {
            features["cpu_usage_high_ratio"],
            features["gpu_high_usage_ratio"],
            features["net_activity_ratio"]
        });

        features["has_system_data"] = 1d;

        return new SystemResourceFeatureResult(features);
    }

    private static double[] Values(IEnumerable<FeatureSignal> events, string key)
    {
        return events
            .Select(e => PayloadValueReader.GetDouble(e.Payload, key))
            .ToArray();
    }

    private static double Mean(IEnumerable<double> values)
    {
        var arr = values as double[] ?? values.ToArray();
        return arr.Length == 0 ? 0d : arr.Average();
    }

    private static double Max(IEnumerable<double> values)
    {
        var arr = values as double[] ?? values.ToArray();
        return arr.Length == 0 ? 0d : arr.Max();
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var arr = values as double[] ?? values.ToArray();
        if (arr.Length == 0)
        {
            return 0d;
        }

        var mean = arr.Average();
        var variance = arr.Select(v => Math.Pow(v - mean, 2d)).Average();
        return Math.Sqrt(variance);
    }

    private static double Ratio(IEnumerable<double> values, Func<double, bool> predicate)
    {
        var arr = values as double[] ?? values.ToArray();
        if (arr.Length == 0)
        {
            return 0d;
        }

        var count = arr.Count(predicate);
        return count / (double)arr.Length;
    }

    private static double CountSpikes(IReadOnlyList<double> values, double minimumDelta)
    {
        if (values.Count <= 1)
        {
            return 0d;
        }

        var count = 0;
        for (var i = 1; i < values.Count; i++)
        {
            if ((values[i] - values[i - 1]) >= minimumDelta)
            {
                count++;
            }
        }

        return count;
    }

    private static double CountThresholdCrossings(IReadOnlyList<double> values, double threshold)
    {
        if (values.Count <= 1)
        {
            return 0d;
        }

        var count = 0;
        var prevHigh = values[0] > threshold;
        for (var i = 1; i < values.Count; i++)
        {
            var currentHigh = values[i] > threshold;
            if (!prevHigh && currentHigh)
            {
                count++;
            }

            prevHigh = currentHigh;
        }

        return count;
    }
}
