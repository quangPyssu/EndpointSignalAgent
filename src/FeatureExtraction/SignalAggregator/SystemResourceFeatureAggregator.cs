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
        var cpuStdValues = OptionalValues(inWindow, "cpu_std_pct");
        var cpuMaxValues = OptionalValues(inWindow, "cpu_max_pct");
        var cpuHighRatioValues = OptionalValues(inWindow, "cpu_high_ratio");
        var cpuSpikeValues = OptionalValues(inWindow, "cpu_spike_count");
        var memStdValues = OptionalValues(inWindow, "mem_std_used_pct");
        var memPressureRatioValues = OptionalValues(inWindow, "mem_pressure_ratio");
        var netRxStdValues = OptionalValues(inWindow, "net_rx_std_kbps");
        var netTxStdValues = OptionalValues(inWindow, "net_tx_std_kbps");
        var cpuAvailableFlags = BoolValues(inWindow, "cpu_available");
        var memAvailableFlags = BoolValues(inWindow, "mem_available");
        var gpuAvailableFlags = BoolValues(inWindow, "gpu_available");
        var gpuIsAvailableInWindow = gpuAvailableFlags.Any(v => v);

        features["cpu_usage_mean"] = Mean(cpuMean);
        features["cpu_usage_max"] = cpuMaxValues.Length > 0 ? Max(cpuMaxValues) : Max(cpuMean);
        features["cpu_usage_std"] = cpuStdValues.Length > 0 ? Mean(cpuStdValues) : StdDev(cpuMean);
        features["cpu_usage_high_ratio"] = cpuHighRatioValues.Length > 0 ? Mean(cpuHighRatioValues) : Ratio(cpuMean, x => x > 80d);
        features["cpu_spike_count"] = cpuSpikeValues.Length > 0 ? cpuSpikeValues.Sum() : CountSpikes(cpuMean, 20d);

        features["ram_usage_mean"] = Mean(memMean);
        features["ram_usage_max"] = Max(memMean);
        features["ram_usage_std"] = memStdValues.Length > 0 ? Mean(memStdValues) : StdDev(memMean);
        features["ram_high_usage_ratio"] = memPressureRatioValues.Length > 0 ? Mean(memPressureRatioValues) : Ratio(memMean, x => x > 85d);
        features["ram_pressure_events"] = CountThresholdCrossings(memMean, 85d);

        features["gpu_available"] = gpuIsAvailableInWindow ? 1d : 0d;
        if (gpuIsAvailableInWindow)
        {
            features["gpu_usage_mean"] = Mean(gpuMean);
            features["gpu_usage_max"] = Max(gpuMean);
            features["gpu_usage_std"] = StdDev(gpuMean);
            features["gpu_memory_usage_mean"] = Mean(gpuMem);
            features["gpu_high_usage_ratio"] = Ratio(gpuMean, x => x > 70d);
        }
        else
        {
            features["gpu_usage_mean"] = 0d;
            features["gpu_usage_max"] = 0d;
            features["gpu_usage_std"] = 0d;
            features["gpu_memory_usage_mean"] = 0d;
            features["gpu_high_usage_ratio"] = 0d;
        }

        var netTxMean = Mean(netTx);
        var netRxMean = Mean(netRx);
        var netTotalMean = Mean(netTotal);
        var netTotalMax = Max(netTotal);
        features["net_tx_kbps_mean"] = netTxMean;
        features["net_rx_kbps_mean"] = netRxMean;
        features["net_total_kbps_mean"] = netTotalMean;
        features["net_total_kbps_max"] = netTotalMax;
        // Compatibility aliases with historical key names that imply bytes but currently carry kbps.
        features["net_bytes_sent_mean"] = netTxMean;
        features["net_bytes_recv_mean"] = netRxMean;
        features["net_bytes_total_mean"] = netTotalMean;
        features["net_bytes_total_max"] = netTotalMax;
        features["net_activity_ratio"] = Ratio(netTotal, x => x > 1d);
        features["net_throughput_std"] = netRxStdValues.Length > 0 && netTxStdValues.Length > 0
            ? Mean(netRxStdValues.Zip(netTxStdValues, (rx, tx) => rx + tx))
            : StdDev(netTotal);
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

        features["cpu_data_available_ratio"] = Ratio(cpuAvailableFlags, x => x);
        features["mem_data_available_ratio"] = Ratio(memAvailableFlags, x => x);
        features["gpu_data_available_ratio"] = Ratio(gpuAvailableFlags, x => x);
        features["has_system_data"] = 1d;

        return new SystemResourceFeatureResult(features);
    }

    private static double[] Values(IEnumerable<FeatureSignal> events, string key)
    {
        return events
            .Select(e => PayloadValueReader.GetDouble(e.Payload, key))
            .ToArray();
    }

    private static double[] OptionalValues(IEnumerable<FeatureSignal> events, string key)
    {
        var values = new List<double>();
        foreach (var evt in events)
        {
            if (TryGetDouble(evt.Payload, key, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool[] BoolValues(IEnumerable<FeatureSignal> events, string key)
    {
        return events
            .Select(e => PayloadValueReader.TryGetBool(e.Payload, key, out var value) && value)
            .ToArray();
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> payload, string key, out double value)
    {
        value = 0d;
        if (!payload.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
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
