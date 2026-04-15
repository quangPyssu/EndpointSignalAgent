# System Resource Signals and Aggregation

This document explains the **end-to-end system resource pipeline**:

1. `SystemResourceCollector` (signal collection side) in `src/SignalCollection/Collectors/SystemResourceCollector.cs`
2. `SystemResourceFeatureAggregator` (feature extraction side) in `src/FeatureExtraction/SignalAggregator/SystemResourceFeatureAggregator.cs`

It is intended to be implementation-level documentation so behavior can be reasoned about directly from code.

---

## 1) Pipeline Position

`SystemResourceCollector` runs as a hosted background collector. It emits `SignalEventType.SystemResourceSample` payloads through `SignalCollectorBase.WriteSignalAsync(...)` into the broadcaster pipeline.

Those signals are then consumed by `FeatureExtractorService`, and `SystemResourceFeatureAggregator` converts the raw payload keys into ML/analytics-ready feature columns from `FeatureSchema.SystemColumns`.

At a high level:

- **Sampling cadence**: every `2s`
- **Rolling sample window maintained by collector**: `60s`
- **Collector emission cadence**: every `15s` (window summary event)
- **Feature extraction window (service-level)**: fixed sliding windows (`60s` size, `30s` step by default)

This means the feature extractor usually sees multiple system-resource summary events per extraction window.

---

## 2) `SystemResourceCollector` (Signal Collection)

## Responsibilities

`SystemResourceCollector` is responsible for:

- Polling machine resource signals on a fixed interval.
- Keeping an in-memory rolling sample buffer.
- Computing statistical summaries over the current 60-second sample set.
- Emitting a normalized key/value payload as a `SystemResourceSample` signal.
- Degrading gracefully when native APIs or adapters fail.

## Runtime loop

On startup the collector:

- Ensures `spool` directory exists.
- Starts a `PeriodicTimer` at `2s`.
- On each tick:
  1. Capture one `ResourceSample`
  2. Append to `_samples`
  3. Trim anything older than `now - 60s`
  4. If `>= 15s` since last emission, compute + emit summary payload.

Failures in one loop iteration are logged and the loop continues.

## Collected metrics (raw sample level)

### CPU

CPU is obtained from native Win32 `GetSystemTimes`:

- Uses `(kernel + user - idle) / (kernel + user)` between successive samples.
- First successful sample initializes baseline and returns `null` (no delta yet).
- If total delta is zero, falls back to `_lastCpuPercent` (or `0`).
- Final value is clamped `[0, 100]`.

Availability:

- `CpuAvailable = true` when API call succeeded and produced a usable sample.
- API failure logs a warning once until recovery, then silently retries.

### Memory

Memory is obtained via `GlobalMemoryStatusEx`:

- `MemoryUsedPercent` from `dwMemoryLoad`
- `AvailableMemoryMb` from `ullAvailPhys`
- `TotalPhysicalMemoryMb` from `ullTotalPhys` (cached)

Availability:

- `MemoryAvailable = true` when native query succeeds.
- Failure logs warning once until recovery, values default to 0.

### GPU

Current implementation emits GPU as unavailable placeholders:

- `GpuPercent = 0`
- `GpuMemoryUsedPercent = 0`
- `ActiveGpuEngines = 0`
- `GpuAvailable = false`

This is intentional during native API migration and reflected in startup logs.

### Network throughput

Network throughput is derived from NIC cumulative byte counters:

- Enumerates `NetworkInterface.GetAllNetworkInterfaces()`
- Ignores loopback and non-`Up` interfaces
- Sums IPv4 `BytesReceived` / `BytesSent`
- Computes delta over elapsed time from previous sample
- Converts to kilobits per second (`kbps`): `bytes/sec * 8 / 1024`

First sample has no baseline, so emits `0` throughput until the second sample.

Network query exceptions are intentionally swallowed (best-effort behavior).

---

## 3) Collector Emitted Payload Contract (`SystemResourceSample`)

Each emitted event includes availability flags plus summary metrics calculated over the collector's current 60-second `_samples` list.

## Metadata + availability

- `window_sec`
- `cpu_available`
- `mem_available`
- `gpu_available`
- `swap_available` (currently always `false`)

## CPU keys

- `cpu_mean_pct`
- `cpu_std_pct`
- `cpu_max_pct`
- `cpu_high_ratio` (share of samples `> 70%`)
- `cpu_idle_ratio` (share of samples `< 10%`)
- `cpu_spike_count` (delta from previous sample `>= 25`)
- `cpu_bucket_flip_count` (bucket changes across low/mid/high with thresholds `30`, `70`)

## Memory keys

- `mem_mean_used_pct`
- `mem_std_used_pct`
- `mem_pressure_ratio` (share `> 85%`)
- `mem_available_bucket` (`unknown | critical | low | moderate | healthy`)
- `mem_swap_activity` (currently derived from placeholder swap series; effectively `0`)
- `mem_range_pct` (`max - min`)

`mem_available_bucket` is based on available memory % of total physical memory:

- `<10%`: `critical`
- `<25%`: `low`
- `<50%`: `moderate`
- `>=50%`: `healthy`
- Missing memory data/total: `unknown`

## GPU keys

- `gpu_mean_pct`
- `gpu_active_ratio` (share `> 20%`)
- `gpu_spike_count` (delta `>= 30`)
- `gpu_mem_used_pct`
- `gpu_engine_active_count`
- `gpu_bucket_flip_count` (thresholds `20`, `60`)

Given current collector behavior, these are normally zero-valued and unavailable.

## Network keys

- `net_rx_mean_kbps`
- `net_rx_std_kbps`
- `net_tx_mean_kbps`
- `net_tx_std_kbps`
- `net_upload_ratio` = `sum(tx) / (sum(tx)+sum(rx))`

## Emission behavior

- Event type: `SystemResourceSample`
- Emission write timeout: `2s` (`WaitAsync(_emitWriteTimeout)`)
- On timeout: warn once (until a future success), skip that emission.

---

## 4) `SystemResourceFeatureAggregator` (Feature Extraction)

## Input model

`ExtractFeatures(events, window)`:

1. Initializes all `FeatureSchema.SystemColumns` to `0.0`.
2. Filters events where:
   - `Type == SystemResourceSample`
   - `TimestampUtc >= window.StartUtc`
   - `TimestampUtc < window.EndUtc`
3. Sorts by timestamp.
4. Returns zero-filled result if no in-window samples.

## Payload parsing strategy

- **Required numeric values** via `PayloadValueReader.GetDouble(...)` (missing/invalid coerces through reader behavior).
- **Optional values** via `OptionalValues(...)` + `TryGetDouble(...)`.
- **Availability flags** via `PayloadValueReader.TryGetBool(...)`.

This enables compatibility if older/newer payload versions omit some keys.

## Feature outputs (schema-level)

Aggregator fills these output columns:

### CPU output features

- `cpu_usage_mean`: mean of `cpu_mean_pct`
- `cpu_usage_max`: max of optional `cpu_max_pct`, fallback max(`cpu_mean_pct`)
- `cpu_usage_std`: mean optional `cpu_std_pct`, fallback stddev(`cpu_mean_pct`)
- `cpu_usage_high_ratio`: mean optional `cpu_high_ratio`, fallback share of `cpu_mean_pct > 80`
- `cpu_spike_count`: sum optional `cpu_spike_count`, fallback spike count on `cpu_mean_pct` with `>= 20` delta

### RAM output features

- `ram_usage_mean`: mean of `mem_mean_used_pct`
- `ram_usage_max`: max of `mem_mean_used_pct`
- `ram_usage_std`: mean optional `mem_std_used_pct`, fallback stddev(`mem_mean_used_pct`)
- `ram_high_usage_ratio`: mean optional `mem_pressure_ratio`, fallback share of `mem_mean_used_pct > 85`
- `ram_pressure_events`: threshold crossings of `mem_mean_used_pct` above `85`

### GPU output features

- `gpu_available`: `1` if any `gpu_available=true` in window, else `0`
- If available:
  - `gpu_usage_mean`, `gpu_usage_max`, `gpu_usage_std`
  - `gpu_memory_usage_mean`
  - `gpu_high_usage_ratio` (share `gpu_mean_pct > 70`)
- If unavailable: all above set to `0`

### Network output features

From `net_tx_mean_kbps`, `net_rx_mean_kbps`:

- `net_tx_kbps_mean`
- `net_rx_kbps_mean`
- `net_total_kbps_mean`
- `net_total_kbps_max`
- Compatibility aliases:
  - `net_bytes_sent_mean`
  - `net_bytes_recv_mean`
  - `net_bytes_total_mean`
  - `net_bytes_total_max`
  > Note: names say "bytes" but values currently carry kbps.
- `net_activity_ratio`: share of total throughput samples `> 1 kbps`
- `net_throughput_std`: uses optional rx/tx std payloads when available, else stddev(total)
- `net_spike_count`: spikes in total throughput with delta `>= 1000`

### Derived cross-resource output features (within system aggregator)

- `system_load_index`
  - GPU available: `0.4*CPU + 0.35*RAM + 0.25*GPU`
  - GPU unavailable: `0.55*CPU + 0.45*RAM`
- `resource_variability_index`: mean of CPU std, RAM std, network std (+ GPU std if available)
- `cpu_ram_correlation_proxy`: `abs(cpu_usage_mean - ram_usage_mean)`
- `active_resource_ratio`: mean of CPU high ratio + network activity ratio (+ GPU high ratio if available)

### Data-quality flags

- `cpu_data_available_ratio`: ratio of in-window events with `cpu_available=true`
- `mem_data_available_ratio`: ratio of in-window events with `mem_available=true`
- `gpu_data_available_ratio`: ratio of in-window events with `gpu_available=true`
- `has_system_data`: always `1` when at least one system-resource event exists in window

---

## 5) Collector ↔ Aggregator Key Mapping

The table below captures the direct mapping used today.

| Collector payload key | Aggregator usage |
|---|---|
| `cpu_mean_pct` | base series for `cpu_usage_mean` and fallbacks |
| `cpu_std_pct` | optional direct source for `cpu_usage_std` |
| `cpu_max_pct` | optional direct source for `cpu_usage_max` |
| `cpu_high_ratio` | optional direct source for `cpu_usage_high_ratio` |
| `cpu_spike_count` | optional direct source for `cpu_spike_count` |
| `mem_mean_used_pct` | base series for RAM features |
| `mem_std_used_pct` | optional direct source for `ram_usage_std` |
| `mem_pressure_ratio` | optional direct source for `ram_high_usage_ratio` |
| `gpu_mean_pct` | GPU usage features (when `gpu_available`) |
| `gpu_mem_used_pct` | `gpu_memory_usage_mean` |
| `net_tx_mean_kbps` | TX + total throughput features |
| `net_rx_mean_kbps` | RX + total throughput features |
| `net_tx_std_kbps` + `net_rx_std_kbps` | preferred source for `net_throughput_std` |
| `cpu_available` | `cpu_data_available_ratio` |
| `mem_available` | `mem_data_available_ratio` |
| `gpu_available` | `gpu_available`, `gpu_data_available_ratio` |

Unused collector payload keys are still preserved in signal data for forward compatibility and diagnostics.

---

## 6) Edge Cases and Operational Notes

- **No events in extractor window**: returns all-zero system feature vector.
- **No GPU support/data**: GPU features remain validly zero and `gpu_available=0`.
- **Startup phase**:
  - CPU may be unavailable for first sample (delta not yet established).
  - Network throughput is zero until second network sample baseline exists.
- **Collector timeout on emit**: one window snapshot can be dropped; next cadence continues.
- **Partial payload compatibility**: aggregator fallback logic prevents hard failures when optional keys are absent.

---

## 7) Current Known Limitation

The collector currently logs that GPU and swap collection are temporarily disabled during native API migration, so downstream GPU/swap features primarily act as placeholders until GPU collection is re-enabled.

