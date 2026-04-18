# System Resource Signals and Aggregation (Raw-Export-First)

This document describes the current system resource path:

1. `SystemResourceCollector` emits raw `SystemResourceTick` observations every 2 seconds.
2. Raw observations are persisted as canonical JSONL envelopes (`raw-collector-v1`).
3. `SystemResourceFeatureAggregator` computes windowed features from raw tick event-time data.

## Collector behavior

`SystemResourceCollector` is a raw sampler/emitter.

- Poll interval: `2s` (`PeriodicTimer`).
- Signal type: `SystemResourceTick`.
- Signal kind: `state_sample`.
- Native cadence: `2` seconds.
- Native aggregation: `null`.
- No rolling 60-second sample buffer.
- No 15-second summary emission path.

On each timer tick it:

1. Captures one raw `ResourceSample` via `SystemResourceSampler`.
2. Emits one `SystemResourceTick` immediately.

The sampler keeps best-effort behavior:

- CPU first sample initializes baseline and emits `cpu_available=false` until a delta is available.
- Network first sample can emit `net_rx_kbps=0`, `net_tx_kbps=0` until byte delta baseline exists.
- GPU sampler absence/failure remains non-fatal (`gpu_available=false`, numeric GPU fields `0`).
- Memory or CPU query failures log once and continue with availability flags set false.

## `SystemResourceTick` payload contract

Collector payload fields are raw near-instantaneous observations only:

- `cpu_available` (bool)
- `cpu_pct` (double)
- `mem_available` (bool)
- `mem_used_pct` (double)
- `mem_avail_mb` (double)
- `mem_total_mb` (double)
- `gpu_available` (bool)
- `gpu_pct` (double)
- `gpu_mem_used_pct` (double)
- `gpu_engine_active_count` (int)
- `swap_available` (bool, currently always `false`)
- `net_rx_kbps` (double)
- `net_tx_kbps` (double)

## Removed collector-side summary keys

The collector no longer emits summary statistics:

- `window_sec`
- `cpu_mean_pct`, `cpu_std_pct`, `cpu_max_pct`, `cpu_high_ratio`, `cpu_idle_ratio`, `cpu_spike_count`, `cpu_bucket_flip_count`
- `mem_mean_used_pct`, `mem_std_used_pct`, `mem_pressure_ratio`, `mem_available_bucket`, `mem_swap_activity`, `mem_range_pct`
- `gpu_mean_pct`, `gpu_active_ratio`, `gpu_spike_count`, `gpu_bucket_flip_count`
- `net_rx_mean_kbps`, `net_rx_std_kbps`, `net_tx_mean_kbps`, `net_tx_std_kbps`, `net_upload_ratio`

These are now feature-extraction responsibilities.

## Raw envelope shape

Each emitted tick is written as one JSONL record in `raw_signals.jsonl`:

- `schema_version = "raw-collector-v1"`
- `collector = "SystemResourceCollector"`
- `signal_type = "SystemResourceTick"`
- `signal_kind = "state_sample"`
- `native_cadence_sec = 2`
- `native_aggregation_sec = null`
- `collector_schema_version = "2.0"`
- `payload = { ...SystemResourceTick fields... }`

## Feature aggregation responsibilities

`SystemResourceFeatureAggregator` consumes `SystemResourceTick` directly and computes per-window features using event time:

- Mean/max/std for CPU, RAM, GPU, network totals.
- High-usage ratios and spike counts from raw series deltas.
- Availability ratios from raw availability flags.
- Derived system indices (`system_load_index`, `resource_variability_index`, etc.).

No compatibility parsing from legacy `SystemResourceSample` summary keys is used.
