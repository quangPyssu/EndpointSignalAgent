using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private readonly TimeSpan _emitWriteTimeout = TimeSpan.FromSeconds(2);
    private readonly List<ResourceSample> _samples = new();
    private DateTimeOffset _lastEmitUtc = DateTimeOffset.MinValue;
    private long? _previousNetworkBytesReceived;
    private long? _previousNetworkBytesSent;
    private DateTimeOffset? _previousNetworkSampleUtc;
    private CpuSampleState _cpuSampleState;
    private double? _lastCpuPercent;
    private bool _loggedSystemTimesFailure;
    private bool _loggedMemoryStatusFailure;
    private bool _loggedEmitTimeout;

    private double? _totalPhysicalMemoryMb;

    private sealed record MemorySnapshot(
        double MemoryUsedPercent,
        double AvailableMemoryMb,
        double TotalPhysicalMemoryMb);

    private struct CpuSampleState
    {
        public ulong IdleTicks;
        public ulong KernelTicks;
        public ulong UserTicks;
        public bool HasPreviousSample;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DwLowDateTime;
        public uint DwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private sealed record ResourceSample(
        DateTimeOffset TimestampUtc,
        double CpuPercent,
        bool CpuAvailable,
        double MemoryUsedPercent,
        bool MemoryAvailable,
        double AvailableMemoryMb,
        double SwapActivityRate,
        double GpuPercent,
        bool GpuAvailable,
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
        _logger.LogInformation("SystemResourceCollector CPU/memory sampling switched to native Win32 APIs.");
        _logger.LogInformation("SystemResourceCollector GPU and swap collection temporarily disabled during native API migration.");

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
        var cpuResult = QueryCpuUsagePercent();
        var cpuAvailable = cpuResult is not null;
        var cpu = cpuResult ?? 0d;
        var memoryResult = QueryMemorySnapshot();

        var memoryAvailable = memoryResult is not null;
        var memUsed = memoryResult?.MemoryUsedPercent ?? 0d;
        var availMb = memoryResult?.AvailableMemoryMb ?? 0d;
        _totalPhysicalMemoryMb = memoryResult?.TotalPhysicalMemoryMb ?? _totalPhysicalMemoryMb ?? 0d;
        var swap = 0d;

        var gpuPercent = 0d;
        var gpuAvailable = false;
        var gpuMemoryUsedPercent = 0d;
        var activeEngines = 0;
        QueryNetworkKbps(now, out var networkRxKbps, out var networkTxKbps);

        return new ResourceSample(
            now,
            cpu,
            cpuAvailable,
            memUsed,
            memoryAvailable,
            availMb,
            swap,
            gpuPercent,
            gpuAvailable,
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
        var swapSeries = _samples.Select(s => s.SwapActivityRate).ToArray();
        var networkRxSeries = _samples.Select(s => s.NetworkRxKbps).ToArray();
        var networkTxSeries = _samples.Select(s => s.NetworkTxKbps).ToArray();
        var latest = _samples[^1];
        var totalUpload = networkTxSeries.Sum();
        var totalTraffic = totalUpload + networkRxSeries.Sum();
        var cpuAvailable = _samples.Any(s => s.CpuAvailable);
        var memoryAvailable = _samples.Any(s => s.MemoryAvailable);
        var gpuAvailable = _samples.Any(s => s.GpuAvailable);

        var payload = new Dictionary<string, string>
        {
            ["window_sec"] = ((int)_window.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["cpu_available"] = cpuAvailable.ToString().ToLowerInvariant(),
            ["mem_available"] = memoryAvailable.ToString().ToLowerInvariant(),
            ["gpu_available"] = gpuAvailable.ToString().ToLowerInvariant(),
            ["swap_available"] = bool.FalseString.ToLowerInvariant(),

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
            ["mem_available_bucket"] = ComputeMemoryAvailableBucket(latest.AvailableMemoryMb, latest.MemoryAvailable),
            ["mem_swap_activity"] = ToInvariant(Mean(swapSeries)),
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

        try
        {
            await WriteSignalAsync(SignalEventType.SystemResourceSample, payload).WaitAsync(_emitWriteTimeout);
            _loggedEmitTimeout = false;
        }
        catch (TimeoutException)
        {
            if (!_loggedEmitTimeout)
            {
                _logger.LogWarning("SystemResourceCollector emit timed out after {TimeoutMs} ms; skipping this emit.", _emitWriteTimeout.TotalMilliseconds);
                _loggedEmitTimeout = true;
            }
        }
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

    private string ComputeMemoryAvailableBucket(double availableMb, bool memoryAvailable)
    {
        if (!memoryAvailable || _totalPhysicalMemoryMb <= 0)
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

    private double? QueryCpuUsagePercent()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            if (!_loggedSystemTimesFailure)
            {
                _logger.LogWarning("GetSystemTimes failed; defaulting CPU usage samples to 0 until recovery.");
                _loggedSystemTimesFailure = true;
            }

            return null;
        }

        _loggedSystemTimesFailure = false;

        var idleTicks = ToUInt64(idleTime);
        var kernelTicks = ToUInt64(kernelTime);
        var userTicks = ToUInt64(userTime);

        if (!_cpuSampleState.HasPreviousSample)
        {
            _cpuSampleState = new CpuSampleState
            {
                IdleTicks = idleTicks,
                KernelTicks = kernelTicks,
                UserTicks = userTicks,
                HasPreviousSample = true
            };

            return null;
        }

        var idleDelta = idleTicks >= _cpuSampleState.IdleTicks ? idleTicks - _cpuSampleState.IdleTicks : 0UL;
        var kernelDelta = kernelTicks >= _cpuSampleState.KernelTicks ? kernelTicks - _cpuSampleState.KernelTicks : 0UL;
        var userDelta = userTicks >= _cpuSampleState.UserTicks ? userTicks - _cpuSampleState.UserTicks : 0UL;
        var total = kernelDelta + userDelta;
        var busy = total > idleDelta ? total - idleDelta : 0UL;

        _cpuSampleState.IdleTicks = idleTicks;
        _cpuSampleState.KernelTicks = kernelTicks;
        _cpuSampleState.UserTicks = userTicks;

        var cpuPercent = total <= 0
            ? _lastCpuPercent ?? 0d
            : (busy / (double)total) * 100d;

        cpuPercent = Math.Clamp(cpuPercent, 0d, 100d);
        _lastCpuPercent = cpuPercent;
        return cpuPercent;
    }

    private MemorySnapshot? QueryMemorySnapshot()
    {
        var memoryStatus = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref memoryStatus))
        {
            if (!_loggedMemoryStatusFailure)
            {
                _logger.LogWarning("GlobalMemoryStatusEx failed; defaulting memory usage samples to 0 until recovery.");
                _loggedMemoryStatusFailure = true;
            }

            return null;
        }

        _loggedMemoryStatusFailure = false;
        return new MemorySnapshot(
            memoryStatus.dwMemoryLoad,
            memoryStatus.ullAvailPhys / 1024d / 1024d,
            memoryStatus.ullTotalPhys / 1024d / 1024d);
    }

    private static ulong ToUInt64(FILETIME fileTime) =>
        ((ulong)fileTime.DwHighDateTime << 32) | fileTime.DwLowDateTime;

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
