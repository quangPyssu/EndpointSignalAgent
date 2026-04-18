using EndpointSignalAgent.Shared.Contracts;

namespace EndpointSignalAgent.FeatureExtraction.SignalAggregator;

/// <summary>
/// Resource features sourced from raw SystemResourceTick state samples.
/// </summary>
internal sealed class SystemResourceFeatureAggregator
{
    public SystemResourceFeatureResult ExtractFeatures(
        IReadOnlyList<FeatureSignal> events,
        SlidingWindow window)
    {
        var features = FeatureSchema.SystemColumns.ToDictionary(column => column, _ => 0.0, StringComparer.Ordinal);

        var inWindow = events
            .Where(e => e.Type == SignalEventType.SystemResourceTick)
            .Where(e => e.TimestampUtc >= window.StartUtc && e.TimestampUtc < window.EndUtc)
            .OrderBy(e => e.TimestampUtc)
            .ToList();

        if (inWindow.Count == 0)
        {
            return new SystemResourceFeatureResult(features);
        }

        var cpuAvailableFlags = BoolValues(inWindow, "cpu_available");
        var memAvailableFlags = BoolValues(inWindow, "mem_available");
        var gpuAvailableFlags = BoolValues(inWindow, "gpu_available");

        var cpu = ValuesWhenAvailable(inWindow, "cpu_pct", "cpu_available");
        var mem = ValuesWhenAvailable(inWindow, "mem_used_pct", "mem_available");
        var gpu = ValuesWhenAvailable(inWindow, "gpu_pct", "gpu_available");
        var gpuMem = ValuesWhenAvailable(inWindow, "gpu_mem_used_pct", "gpu_available");
        var netTx = Values(inWindow, "net_tx_kbps");
        var netRx = Values(inWindow, "net_rx_kbps");
        var netTotal = netTx.Zip(netRx, (tx, rx) => tx + rx).ToArray();

        features["cpu_usage_mean"] = Mean(cpu);
        features["cpu_usage_max"] = Max(cpu);
        features["cpu_usage_std"] = StdDev(cpu);
        features["cpu_usage_high_ratio"] = Ratio(cpu, x => x > 80d);
        features["cpu_spike_count"] = CountSpikes(cpu, 20d);

        features["ram_usage_mean"] = Mean(mem);
        features["ram_usage_max"] = Max(mem);
        features["ram_usage_std"] = StdDev(mem);
        features["ram_high_usage_ratio"] = Ratio(mem, x => x > 85d);
        features["ram_pressure_events"] = CountThresholdCrossings(mem, 85d);

        var gpuIsAvailableInWindow = gpuAvailableFlags.Any(v => v);
        features["gpu_available"] = gpuIsAvailableInWindow ? 1d : 0d;
        features["gpu_usage_mean"] = Mean(gpu);
        features["gpu_usage_max"] = Max(gpu);
        features["gpu_usage_std"] = StdDev(gpu);
        features["gpu_memory_usage_mean"] = Mean(gpuMem);
        features["gpu_high_usage_ratio"] = Ratio(gpu, x => x > 70d);

        var netTxMean = Mean(netTx);
        var netRxMean = Mean(netRx);
        var netTotalMean = Mean(netTotal);
        var netTotalMax = Max(netTotal);
        features["net_tx_kbps_mean"] = netTxMean;
        features["net_rx_kbps_mean"] = netRxMean;
        features["net_total_kbps_mean"] = netTotalMean;
        features["net_total_kbps_max"] = netTotalMax;
        features["net_bytes_sent_mean"] = netTxMean;
        features["net_bytes_recv_mean"] = netRxMean;
        features["net_bytes_total_mean"] = netTotalMean;
        features["net_bytes_total_max"] = netTotalMax;
        features["net_activity_ratio"] = Ratio(netTotal, x => x > 1d);
        features["net_throughput_std"] = StdDev(netTotal);
        features["net_spike_count"] = CountSpikes(netTotal, 1000d);

        features["system_load_index"] = gpuIsAvailableInWindow
            ? (features["cpu_usage_mean"] * 0.4d) + (features["ram_usage_mean"] * 0.35d) + (features["gpu_usage_mean"] * 0.25d)
            : (features["cpu_usage_mean"] * 0.55d) + (features["ram_usage_mean"] * 0.45d);

        var variabilityComponents = new List<double>
        {
            features["cpu_usage_std"],
            features["ram_usage_std"],
            features["net_throughput_std"]
        };
        if (gpuIsAvailableInWindow)
        {
            variabilityComponents.Add(features["gpu_usage_std"]);
        }

        features["resource_variability_index"] = Mean(variabilityComponents);
        features["cpu_ram_correlation_proxy"] = Math.Abs(features["cpu_usage_mean"] - features["ram_usage_mean"]);

        var activeResourceComponents = new List<double>
        {
            features["cpu_usage_high_ratio"],
            features["net_activity_ratio"]
        };
        if (gpuIsAvailableInWindow)
        {
            activeResourceComponents.Add(features["gpu_high_usage_ratio"]);
        }

        features["active_resource_ratio"] = Mean(activeResourceComponents);
        features["cpu_data_available_ratio"] = Ratio(cpuAvailableFlags);
        features["mem_data_available_ratio"] = Ratio(memAvailableFlags);
        features["gpu_data_available_ratio"] = Ratio(gpuAvailableFlags);
        features["has_system_data"] = 1d;

        return new SystemResourceFeatureResult(features);
    }

    private static double[] Values(IEnumerable<FeatureSignal> events, string key)
    {
        return events.Select(e => PayloadValueReader.GetDouble(e.Payload, key)).ToArray();
    }

    private static double[] ValuesWhenAvailable(IEnumerable<FeatureSignal> events, string valueKey, string availableKey)
    {
        return events
            .Where(e => PayloadValueReader.TryGetBool(e.Payload, availableKey, out var available) && available)
            .Select(e => PayloadValueReader.GetDouble(e.Payload, valueKey))
            .ToArray();
    }

    private static bool[] BoolValues(IEnumerable<FeatureSignal> events, string key)
    {
        return events
            .Select(e => PayloadValueReader.TryGetBool(e.Payload, key, out var value) && value)
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

    private static double Ratio(IEnumerable<bool> values)
    {
        var arr = values as bool[] ?? values.ToArray();
        if (arr.Length == 0)
        {
            return 0d;
        }

        var count = arr.Count(v => v);
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
