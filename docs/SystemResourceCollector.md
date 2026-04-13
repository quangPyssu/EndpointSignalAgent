# SystemResourceCollector

`SystemResourceCollector` collects windowed CPU, memory, GPU, and network utilization data and emits summarized signals on a fixed cadence.

## How it runs

- Starts a `PeriodicTimer` on a 2-second poll interval.
- Each tick:
  - Captures a raw sample (CPU, memory, GPU, network).
  - Stores it in an in-memory list.
  - Trims samples older than a 60-second window.
- Every 15 seconds, aggregates the current window into a summary payload and emits a `SystemResourceSample` signal.

## Data sources

All metrics are collected using Windows performance counters (WMI queries):

- CPU: `Win32_PerfFormattedData_PerfOS_Processor` (`PercentProcessorTime`, `_Total`).
- Memory: `Win32_PerfFormattedData_PerfOS_Memory` (`PercentCommittedBytesInUse`, `AvailableMBytes`, `PagesInputPersec`, `PageReadsPersec`).
- GPU engine usage: `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine` (`UtilizationPercentage`).
- GPU memory usage: `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory` (`DedicatedUsage`, `SharedUsage`).
- Total physical memory (for bucketization): `Win32_OperatingSystem` (`TotalVisibleMemorySize`).
- Network: `NetworkInterface.GetAllNetworkInterfaces()` and IPv4 stats (bytes sent/received), converted to kbps over the polling interval.

All queries are best-effort. If a query fails, the collector logs a warning (when appropriate) and continues with default values.

## Aggregation logic

For the last 60 seconds of samples, the collector computes:

- Mean, standard deviation, max, and ratio-based buckets for CPU and memory.
- Spike counts and bucket flip counts for CPU/GPU variability.
- Swap activity and memory availability buckets.
- Mean/std and upload ratio for network traffic.

The computed fields are serialized as strings and sent in the signal payload.

## Output

Signals are written to the configured broadcaster and ultimately into `spool\signals.jsonl` via `SignalCollectorBase`. The collector uses a 2-second timeout when emitting a windowed signal.

## Error handling

The collector:

- Catches and ignores exceptions during individual metric queries.
- Logs warnings for unexpected failures in the polling loop.
- Continues running until the host is stopped or cancellation is requested.
