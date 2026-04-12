using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;

namespace EndpointSignalAgent.SignalCollection.Collectors;

/// <summary>
/// Collects windowed CPU, memory, and GPU utilization signals.
/// </summary>
public sealed class SystemResourceCollector : SignalCollectorBase
{
    private readonly ILogger<SystemResourceCollector> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _window = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _emitInterval = TimeSpan.FromSeconds(15);
    private readonly List<ResourceSample> _samples = new();
    private DateTimeOffset _lastEmitUtc = DateTimeOffset.MinValue;
    private long? _previousNetworkBytesReceived;
    private long? _previousNetworkBytesSent;
    private DateTimeOffset? _previousNetworkSampleUtc;

    private double? _totalPhysicalMemoryMb;

    private sealed record ResourceSample(
        DateTimeOffset TimestampUtc,
        double CpuPercent,
        double MemoryUsedPercent,
        double AvailableMemoryMb,
        double SwapActivityRate,
        double GpuPercent,
        double GpuMemoryUsedPercent,
        int ActiveGpuEngines,
        double NetworkRxKbps,
        double NetworkTxKbps);

    public SystemResourceCollector(
        ILogger<SystemResourceCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl)
        : base(@"spool\signals.jsonl", broadcaster, collectionControl)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");
        _logger.LogInformation("SystemResourceCollector started.");

        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var sample = CaptureSample(now);
                _samples.Add(sample);
                TrimOldSamples(now);

                if (now - _lastEmitUtc >= _emitInterval)
                {
                    _lastEmitUtc = now;
                    await EmitWindowSignalsAsync(now);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemResourceCollector loop error.");
            }
        }

        _logger.LogInformation("SystemResourceCollector stopped.");
    }

    private ResourceSample CaptureSample(DateTimeOffset now)
    {
        var cpu = QueryCpuUsagePercent() ?? 0d;

        var memUsed = QueryMemoryUsedPercent() ?? 0d;
        var availMb = QueryAvailableMemoryMb() ?? 0d;
        var swap = QuerySwapActivityRate() ?? 0d;

        var gpuPercent = QueryGpuUsagePercent(out var activeEngines) ?? 0d;
        var gpuMemoryUsedPercent = QueryGpuMemoryUsedPercent() ?? 0d;
        var networkRxKbps = QueryNetworkBytesPerSecond("BytesReceivedPersec")
            .GetValueOrDefault() * 8d / 1024d;
        var networkTxKbps = QueryNetworkBytesPerSecond("BytesSentPersec")
            .GetValueOrDefault() * 8d / 1024d;

        return new ResourceSample(
            now,
            cpu,
            memUsed,
            availMb,
            swap,
            gpuPercent,
            gpuMemoryUsedPercent,
            activeEngines,
            networkRxKbps,
            networkTxKbps);
    }

    private async Task EmitWindowSignalsAsync(DateTimeOffset now)
    {
        if (_samples.Count == 0)
        {
            return;
        }

        var cpuSeries = _samples.Select(s => s.CpuPercent).ToArray();
        var memSeries = _samples.Select(s => s.MemoryUsedPercent).ToArray();
        var gpuSeries = _samples.Select(s => s.GpuPercent).ToArray();
        var networkRxSeries = _samples.Select(s => s.NetworkRxKbps).ToArray();
        var networkTxSeries = _samples.Select(s => s.NetworkTxKbps).ToArray();
        var latest = _samples[^1];
        var totalUpload = networkTxSeries.Sum();
        var totalTraffic = totalUpload + networkRxSeries.Sum();

        var payload = new Dictionary<string, string>
        {
            ["window_sec"] = ((int)_window.TotalSeconds).ToString(CultureInfo.InvariantCulture),

            ["cpu_mean_pct"] = ToInvariant(Mean(cpuSeries)),
            ["cpu_std_pct"] = ToInvariant(StdDev(cpuSeries)),
            ["cpu_max_pct"] = ToInvariant(Max(cpuSeries)),
            ["cpu_high_ratio"] = ToInvariant(Ratio(cpuSeries, x => x > 70d)),
            ["cpu_idle_ratio"] = ToInvariant(Ratio(cpuSeries, x => x < 10d)),
            ["cpu_spike_count"] = CountSpikes(cpuSeries, 25d).ToString(CultureInfo.InvariantCulture),
            ["cpu_bucket_flip_count"] = CountBucketFlips(cpuSeries, 30d, 70d).ToString(CultureInfo.InvariantCulture),

            ["mem_mean_used_pct"] = ToInvariant(Mean(memSeries)),
            ["mem_std_used_pct"] = ToInvariant(StdDev(memSeries)),
            ["mem_pressure_ratio"] = ToInvariant(Ratio(memSeries, x => x > 85d)),
            ["mem_available_bucket"] = ComputeMemoryAvailableBucket(latest.AvailableMemoryMb),
            ["mem_swap_activity"] = ToInvariant(Mean(_samples.Select(s => s.SwapActivityRate))),
            ["mem_range_pct"] = ToInvariant(Range(memSeries)),

            ["gpu_mean_pct"] = ToInvariant(Mean(gpuSeries)),
            ["gpu_active_ratio"] = ToInvariant(Ratio(gpuSeries, x => x > 20d)),
            ["gpu_spike_count"] = CountSpikes(gpuSeries, 30d).ToString(CultureInfo.InvariantCulture),
            ["gpu_mem_used_pct"] = ToInvariant(latest.GpuMemoryUsedPercent),
            ["gpu_engine_active_count"] = latest.ActiveGpuEngines.ToString(CultureInfo.InvariantCulture),
            ["gpu_bucket_flip_count"] = CountBucketFlips(gpuSeries, 20d, 60d).ToString(CultureInfo.InvariantCulture),

            ["net_rx_mean_kbps"] = ToInvariant(Mean(networkRxSeries)),
            ["net_rx_std_kbps"] = ToInvariant(StdDev(networkRxSeries)),
            ["net_tx_mean_kbps"] = ToInvariant(Mean(networkTxSeries)),
            ["net_tx_std_kbps"] = ToInvariant(StdDev(networkTxSeries)),
            ["net_upload_ratio"] = ToInvariant(totalTraffic <= 0d ? 0d : totalUpload / totalTraffic)
        };

        await WriteSignalAsync(SignalEventType.SystemResourceSample, payload);
    }

    private void TrimOldSamples(DateTimeOffset now)
    {
        var threshold = now - _window;
        _samples.RemoveAll(s => s.TimestampUtc < threshold);
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

    private static double Range(IEnumerable<double> values)
    {
        var arr = values as double[] ?? values.ToArray();
        return arr.Length == 0 ? 0d : arr.Max() - arr.Min();
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

    private static int CountSpikes(IReadOnlyList<double> values, double minimumDelta)
    {
        if (values.Count <= 1)
        {
            return 0;
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

    private static int CountBucketFlips(IReadOnlyList<double> values, double lowUpperBound, double midUpperBound)
    {
        if (values.Count <= 1)
        {
            return 0;
        }

        int Bucket(double v) => v < lowUpperBound ? 0 : v < midUpperBound ? 1 : 2;

        var flips = 0;
        var previous = Bucket(values[0]);
        for (var i = 1; i < values.Count; i++)
        {
            var current = Bucket(values[i]);
            if (current != previous)
            {
                flips++;
            }

            previous = current;
        }

        return flips;
    }

    private static string ToInvariant(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private string ComputeMemoryAvailableBucket(double availableMb)
    {
        _totalPhysicalMemoryMb ??= QueryTotalPhysicalMemoryMb() ?? 0d;
        if (_totalPhysicalMemoryMb <= 0)
        {
            return "unknown";
        }

        var availablePercent = (availableMb / _totalPhysicalMemoryMb.Value) * 100d;
        if (availablePercent < 10d)
        {
            return "critical";
        }

        if (availablePercent < 25d)
        {
            return "low";
        }

        if (availablePercent < 50d)
        {
            return "moderate";
        }

        return "healthy";
    }

    private static double? QueryCpuUsagePercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDouble(obj["PercentProcessorTime"], CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // ignored by design - collector emits best-effort values.
        }

        return null;
    }

    private static double? QueryMemoryUsedPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PercentCommittedBytesInUse FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDouble(obj["PercentCommittedBytesInUse"], CultureInfo.InvariantCulture);
            }
        }
        catch
        {
        }

        return null;
    }

    private static double? QueryAvailableMemoryMb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT AvailableMBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementObject obj in searcher.Get())
            {
                return Convert.ToDouble(obj["AvailableMBytes"], CultureInfo.InvariantCulture);
            }
        }
        catch
        {
        }

        return null;
    }

    private static double? QuerySwapActivityRate()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT PagesInputPersec, PageReadsPersec FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (ManagementObject obj in searcher.Get())
            {
                var pagesInput = Convert.ToDouble(obj["PagesInputPersec"], CultureInfo.InvariantCulture);
                var pageReads = Convert.ToDouble(obj["PageReadsPersec"], CultureInfo.InvariantCulture);
                return pagesInput + pageReads;
            }
        }
        catch
        {
        }

        return null;
    }

    private static double? QueryGpuUsagePercent(out int activeEngineCount)
    {
        activeEngineCount = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            var total = 0d;
            var samples = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = Convert.ToString(obj["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (name.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var usage = Convert.ToDouble(obj["UtilizationPercentage"], CultureInfo.InvariantCulture);
                total += usage;
                samples++;
                if (usage > 5d)
                {
                    activeEngineCount++;
                }
            }

            if (samples == 0)
            {
                return 0d;
            }

            return Math.Clamp(total, 0d, 100d);
        }
        catch
        {
            return null;
        }
    }

    private static double? QueryGpuMemoryUsedPercent()
    {
        try
        {
            using var memSearcher = new ManagementObjectSearcher("SELECT DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            double usedBytes = 0d;
            foreach (ManagementObject obj in memSearcher.Get())
            {
                usedBytes += Convert.ToDouble(obj["DedicatedUsage"], CultureInfo.InvariantCulture);
                usedBytes += Convert.ToDouble(obj["SharedUsage"], CultureInfo.InvariantCulture);
            }

            if (usedBytes <= 0d)
            {
                return 0d;
            }

            // Driver-level dedicated + shared usage can exceed a single adapter capacity,
            // so this is normalized into a bounded heuristic percentage.
            var approxPct = usedBytes / (1024d * 1024d * 1024d) * 10d;
            return Math.Clamp(approxPct, 0d, 100d);
        }
        catch
        {
            return null;
        }
    }

    private static double? QueryTotalPhysicalMemoryMb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var kib = Convert.ToDouble(obj["TotalVisibleMemorySize"], CultureInfo.InvariantCulture);
                return kib / 1024d;
            }
        }
        catch
        {
        }

        return null;
    }

    private void QueryNetworkKbps(DateTimeOffset now, out double rxKbps, out double txKbps)
    {
        rxKbps = 0d;
        txKbps = 0d;

        try
        {
            long totalRxBytes = 0;
            long totalTxBytes = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var stats = nic.GetIPv4Statistics();
                totalRxBytes += stats.BytesReceived;
                totalTxBytes += stats.BytesSent;
            }

            if (_previousNetworkSampleUtc is not null &&
                _previousNetworkBytesReceived is not null &&
                _previousNetworkBytesSent is not null)
            {
                var elapsedSeconds = (now - _previousNetworkSampleUtc.Value).TotalSeconds;
                if (elapsedSeconds > 0d)
                {
                    var rxBytesPerSecond = Math.Max(0d, (totalRxBytes - _previousNetworkBytesReceived.Value) / elapsedSeconds);
                    var txBytesPerSecond = Math.Max(0d, (totalTxBytes - _previousNetworkBytesSent.Value) / elapsedSeconds);
                    rxKbps = rxBytesPerSecond * 8d / 1024d;
                    txKbps = txBytesPerSecond * 8d / 1024d;
                }
            }

            _previousNetworkBytesReceived = totalRxBytes;
            _previousNetworkBytesSent = totalTxBytes;
            _previousNetworkSampleUtc = now;
        }
        catch
        {
            // ignored by design - collector emits best-effort values.
        }
    }
}
