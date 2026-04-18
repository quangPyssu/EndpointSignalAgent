using EndpointSignalAgent.Shared.Contracts;
using EndpointSignalAgent.SignalCollection.Broadcasting;
using EndpointSignalAgent.SignalCollection.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace EndpointSignalAgent.SignalCollection.Collectors;

/// <summary>
/// Collects raw CPU, memory, GPU, and network utilization state samples.
/// </summary>
public sealed class SystemResourceCollector : SignalCollectorBase
{
    private readonly ILogger<SystemResourceCollector> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly SystemResourceSampler _sampler;

    private sealed record ResourceSample(
        DateTimeOffset TimestampUtc,
        bool CpuAvailable,
        double CpuPercent,
        bool MemoryAvailable,
        double MemoryUsedPercent,
        double AvailableMemoryMb,
        double TotalPhysicalMemoryMb,
        bool GpuAvailable,
        double GpuPercent,
        double GpuMemoryUsedPercent,
        int ActiveGpuEngines,
        bool SwapAvailable,
        double NetworkRxKbps,
        double NetworkTxKbps);

    public SystemResourceCollector(
        ILogger<SystemResourceCollector> logger,
        ISignalBroadcaster broadcaster,
        ICollectionControl collectionControl)
        : base(@"spool\signals.jsonl", broadcaster, collectionControl)
    {
        _logger = logger;
        _sampler = new SystemResourceSampler(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory("spool");
        _logger.LogInformation("SystemResourceCollector started.");

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    var sample = _sampler.CaptureSample(now);
                    await WriteSignalAsync(SignalEventType.SystemResourceTick, BuildPayload(sample));
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
        }
        finally
        {
            _sampler.Dispose();
        }

        _logger.LogInformation("SystemResourceCollector stopped.");
    }

    private static Dictionary<string, string> BuildPayload(ResourceSample sample)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cpu_available"] = sample.CpuAvailable.ToString().ToLowerInvariant(),
            ["cpu_pct"] = ToInvariant(sample.CpuPercent),
            ["mem_available"] = sample.MemoryAvailable.ToString().ToLowerInvariant(),
            ["mem_used_pct"] = ToInvariant(sample.MemoryUsedPercent),
            ["mem_avail_mb"] = ToInvariant(sample.AvailableMemoryMb),
            ["mem_total_mb"] = ToInvariant(sample.TotalPhysicalMemoryMb),
            ["gpu_available"] = sample.GpuAvailable.ToString().ToLowerInvariant(),
            ["gpu_pct"] = ToInvariant(sample.GpuPercent),
            ["gpu_mem_used_pct"] = ToInvariant(sample.GpuMemoryUsedPercent),
            ["gpu_engine_active_count"] = sample.ActiveGpuEngines.ToString(CultureInfo.InvariantCulture),
            ["swap_available"] = sample.SwapAvailable.ToString().ToLowerInvariant(),
            ["net_rx_kbps"] = ToInvariant(sample.NetworkRxKbps),
            ["net_tx_kbps"] = ToInvariant(sample.NetworkTxKbps)
        };
    }

    private static string ToInvariant(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class SystemResourceSampler : IDisposable
    {
        private readonly ILogger _logger;
        private CpuSampleState _cpuSampleState;
        private double? _lastCpuPercent;
        private bool _loggedSystemTimesFailure;
        private bool _loggedMemoryStatusFailure;
        private long? _previousNetworkBytesReceived;
        private long? _previousNetworkBytesSent;
        private DateTimeOffset? _previousNetworkSampleUtc;
        private readonly PdhGpuSampler? _gpuSampler;

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

        public SystemResourceSampler(ILogger logger)
        {
            _logger = logger;
            _gpuSampler = PdhGpuSampler.TryCreate(_logger);
            if (_gpuSampler is null)
            {
                _logger.LogInformation("SystemResourceCollector GPU sampling unavailable; collector will emit fallback GPU values.");
            }
        }

        public ResourceSample CaptureSample(DateTimeOffset now)
        {
            var cpuResult = QueryCpuUsagePercent();
            var cpuAvailable = cpuResult is not null;
            var cpu = cpuResult ?? 0d;

            var memoryResult = QueryMemorySnapshot();
            var memoryAvailable = memoryResult is not null;
            var memUsed = memoryResult?.MemoryUsedPercent ?? 0d;
            var availMb = memoryResult?.AvailableMemoryMb ?? 0d;
            var totalMb = memoryResult?.TotalPhysicalMemoryMb ?? 0d;

            var gpuPercent = 0d;
            var gpuAvailable = false;
            var gpuMemoryUsedPercent = 0d;
            var activeEngines = 0;
            if (_gpuSampler?.TrySample(out var sampledGpuPercent, out var sampledGpuMemPct, out var sampledActiveEngineCount) == true)
            {
                gpuPercent = sampledGpuPercent;
                gpuMemoryUsedPercent = sampledGpuMemPct;
                activeEngines = sampledActiveEngineCount;
                gpuAvailable = true;
            }

            QueryNetworkKbps(now, out var networkRxKbps, out var networkTxKbps);

            return new ResourceSample(
                now,
                cpuAvailable,
                cpu,
                memoryAvailable,
                memUsed,
                availMb,
                totalMb,
                gpuAvailable,
                gpuPercent,
                gpuMemoryUsedPercent,
                activeEngines,
                SwapAvailable: false,
                networkRxKbps,
                networkTxKbps);
        }

        public void Dispose()
        {
            _gpuSampler?.Dispose();
        }

        private double? QueryCpuUsagePercent()
        {
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                if (!_loggedSystemTimesFailure)
                {
                    _logger.LogWarning("GetSystemTimes failed; emitting cpu_available=false until recovery.");
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
                    _logger.LogWarning("GlobalMemoryStatusEx failed; emitting mem_available=false until recovery.");
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
                // Best effort by design; baseline will recover on next successful sample.
            }
        }
    }

    private sealed class PdhGpuSampler : IDisposable
    {
        private const uint ErrorSuccess = 0;
        private const uint PdhMoreData = 0x800007D2;
        private const uint PdhFmtDouble = 0x00000200;
        private const uint PdhFmtNoScale = 0x00001000;
        private const uint PdhFmtNoCap100 = 0x00008000;
        private const uint CounterStatusValidData = 0x00000000;
        private const double ActiveEngineThresholdPct = 0.1d;
        private static readonly uint CounterFormat = PdhFmtDouble | PdhFmtNoScale | PdhFmtNoCap100;
        private static readonly StringComparison AdapterIdComparison = StringComparison.OrdinalIgnoreCase;
        private readonly ILogger _logger;
        private readonly IntPtr _queryHandle;
        private readonly IntPtr _engineUtilizationCounter;
        private readonly IntPtr _dedicatedUsageCounter;
        private readonly IntPtr _dedicatedLimitCounter;
        private readonly IntPtr _sharedUsageCounter;
        private readonly IntPtr _sharedLimitCounter;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(
            string? dataSource,
            IntPtr userData,
            out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhCloseQuery(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounter(
            IntPtr query,
            string fullCounterPath,
            IntPtr userData,
            out IntPtr counter);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhGetFormattedCounterArray(
            IntPtr counter,
            uint format,
            ref uint bufferSize,
            out uint itemCount,
            IntPtr itemBuffer);

        private PdhGpuSampler(
            ILogger logger,
            IntPtr queryHandle,
            IntPtr engineUtilizationCounter,
            IntPtr dedicatedUsageCounter,
            IntPtr dedicatedLimitCounter,
            IntPtr sharedUsageCounter,
            IntPtr sharedLimitCounter)
        {
            _logger = logger;
            _queryHandle = queryHandle;
            _engineUtilizationCounter = engineUtilizationCounter;
            _dedicatedUsageCounter = dedicatedUsageCounter;
            _dedicatedLimitCounter = dedicatedLimitCounter;
            _sharedUsageCounter = sharedUsageCounter;
            _sharedLimitCounter = sharedLimitCounter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint CStatus;
            public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValueItem
        {
            public IntPtr Name;
            public PdhFmtCounterValue FmtValue;
        }

        public static PdhGpuSampler? TryCreate(ILogger logger)
        {
            var openStatus = PdhOpenQuery(null, IntPtr.Zero, out var queryHandle);
            if (openStatus != ErrorSuccess)
            {
                logger.LogDebug("PDH GPU query open failed with status 0x{Status:X8}.", openStatus);
                return null;
            }

            try
            {
                if (!TryAddCounter(queryHandle, @"\GPU Engine(*)\Utilization Percentage", out var engineCounter, logger, isRequired: true) ||
                    !TryAddCounter(queryHandle, @"\GPU Adapter Memory(*)\Dedicated Usage", out var dedicatedUsageCounter, logger, isRequired: true) ||
                    !TryAddCounter(queryHandle, @"\GPU Adapter Memory(*)\Shared Usage", out var sharedUsageCounter, logger, isRequired: true))
                {
                    PdhCloseQuery(queryHandle);
                    return null;
                }

                TryAddCounter(queryHandle, @"\GPU Adapter Memory(*)\Dedicated Limit", out var dedicatedLimitCounter, logger, isRequired: false);
                TryAddCounter(queryHandle, @"\GPU Adapter Memory(*)\Shared Limit", out var sharedLimitCounter, logger, isRequired: false);

                _ = PdhCollectQueryData(queryHandle);
                return new PdhGpuSampler(
                    logger,
                    queryHandle,
                    engineCounter,
                    dedicatedUsageCounter,
                    dedicatedLimitCounter,
                    sharedUsageCounter,
                    sharedLimitCounter);
            }
            catch
            {
                PdhCloseQuery(queryHandle);
                return null;
            }
        }

        public bool TrySample(out double gpuPercent, out double gpuMemPct, out int activeEngineCount)
        {
            gpuPercent = 0d;
            gpuMemPct = 0d;
            activeEngineCount = 0;

            try
            {
                var collectStatus = PdhCollectQueryData(_queryHandle);
                if (collectStatus != ErrorSuccess)
                {
                    return false;
                }

                if (!TryReadCounterArray(_engineUtilizationCounter, out var engineValues) ||
                    !TryReadCounterArray(_dedicatedUsageCounter, out var dedicatedUsageValues) ||
                    !TryReadCounterArray(_sharedUsageCounter, out var sharedUsageValues))
                {
                    return false;
                }

                _ = TryReadCounterArray(_dedicatedLimitCounter, out var dedicatedLimitValues);
                _ = TryReadCounterArray(_sharedLimitCounter, out var sharedLimitValues);

                var adapterEngineUtilization = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (instanceName, value) in engineValues)
                {
                    var adapterId = TryGetAdapterId(instanceName);
                    if (string.IsNullOrEmpty(adapterId))
                    {
                        continue;
                    }

                    if (value > ActiveEngineThresholdPct)
                    {
                        activeEngineCount++;
                    }

                    adapterEngineUtilization[adapterId] = adapterEngineUtilization.GetValueOrDefault(adapterId) + value;
                }

                gpuPercent = Math.Clamp(adapterEngineUtilization.Values.Sum(), 0d, 100d);

                var dedicatedUsageByAdapter = AggregateByAdapter(dedicatedUsageValues);
                var sharedUsageByAdapter = AggregateByAdapter(sharedUsageValues);
                var dedicatedLimitByAdapter = AggregateByAdapter(dedicatedLimitValues);
                var sharedLimitByAdapter = AggregateByAdapter(sharedLimitValues);

                var totalUsedBytes = 0d;
                var totalLimitBytes = 0d;
                foreach (var adapterId in dedicatedUsageByAdapter.Keys
                    .Concat(sharedUsageByAdapter.Keys)
                    .Concat(dedicatedLimitByAdapter.Keys)
                    .Concat(sharedLimitByAdapter.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    totalUsedBytes += dedicatedUsageByAdapter.GetValueOrDefault(adapterId)
                        + sharedUsageByAdapter.GetValueOrDefault(adapterId);
                    totalLimitBytes += dedicatedLimitByAdapter.GetValueOrDefault(adapterId)
                        + sharedLimitByAdapter.GetValueOrDefault(adapterId);
                }

                gpuMemPct = totalLimitBytes > 0d
                    ? Math.Clamp((totalUsedBytes / totalLimitBytes) * 100d, 0d, 100d)
                    : 0d;

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PDH GPU sampling failed on this tick; values will be retried.");
                return false;
            }
        }

        public void Dispose()
        {
            _ = PdhCloseQuery(_queryHandle);
        }

        private static bool TryAddCounter(
            IntPtr queryHandle,
            string counterPath,
            out IntPtr counterHandle,
            ILogger logger,
            bool isRequired)
        {
            var status = PdhAddEnglishCounter(queryHandle, counterPath, IntPtr.Zero, out counterHandle);
            if (status == ErrorSuccess)
            {
                return true;
            }

            counterHandle = IntPtr.Zero;
            if (isRequired)
            {
                logger.LogDebug("Required PDH GPU counter {CounterPath} failed with status 0x{Status:X8}.", counterPath, status);
            }
            else
            {
                logger.LogDebug("Optional PDH GPU counter {CounterPath} unavailable (status 0x{Status:X8}); GPU memory percent may be omitted.", counterPath, status);
            }

            return !isRequired;
        }

        private static bool TryReadCounterArray(IntPtr counterHandle, out IReadOnlyList<(string InstanceName, double Value)> values)
        {
            values = [];
            if (counterHandle == IntPtr.Zero)
            {
                return false;
            }

            uint bufferSize = 0;
            var status = PdhGetFormattedCounterArray(counterHandle, CounterFormat, ref bufferSize, out var itemCount, IntPtr.Zero);
            if (status != PdhMoreData && status != ErrorSuccess)
            {
                return false;
            }

            if (bufferSize == 0 || itemCount == 0)
            {
                return true;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            var buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArray(counterHandle, CounterFormat, ref bufferSize, out itemCount, buffer);
                if (status != ErrorSuccess)
                {
                    return false;
                }

                var result = new List<(string InstanceName, double Value)>((int)itemCount);
                for (var i = 0; i < itemCount; i++)
                {
                    var itemPtr = IntPtr.Add(buffer, i * itemSize);
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPtr);
                    if (item.FmtValue.CStatus != CounterStatusValidData || item.Name == IntPtr.Zero)
                    {
                        continue;
                    }

                    var instanceName = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrWhiteSpace(instanceName))
                    {
                        continue;
                    }

                    var value = double.IsFinite(item.FmtValue.DoubleValue) ? item.FmtValue.DoubleValue : 0d;
                    result.Add((instanceName, Math.Max(0d, value)));
                }

                values = result;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static Dictionary<string, double> AggregateByAdapter(IReadOnlyList<(string InstanceName, double Value)> values)
        {
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instanceName, value) in values)
            {
                var adapterId = TryGetAdapterId(instanceName);
                if (string.IsNullOrEmpty(adapterId))
                {
                    continue;
                }

                totals[adapterId] = totals.GetValueOrDefault(adapterId) + value;
            }

            return totals;
        }

        private static string? TryGetAdapterId(string instanceName)
        {
            if (string.IsNullOrWhiteSpace(instanceName))
            {
                return null;
            }

            var luidIndex = instanceName.IndexOf("luid_", AdapterIdComparison);
            if (luidIndex < 0)
            {
                return null;
            }

            var physIndex = instanceName.IndexOf("_phys_", luidIndex, AdapterIdComparison);
            var end = physIndex >= 0 ? physIndex : instanceName.Length;
            var adapterId = instanceName.Substring(luidIndex, end - luidIndex);
            return string.IsNullOrWhiteSpace(adapterId) ? null : adapterId;
        }
    }
}