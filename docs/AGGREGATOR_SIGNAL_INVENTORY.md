# Aggregator Signal Inventory

> Migration note: canonical collector export for replay is now `spool/raw_signals.jsonl` with schema `raw-collector-v1`. Legacy `spool/signals.jsonl` remains for backend send compatibility during migration.

This document lists the signals emitted by collectors that are consumed by the feature aggregators.

Scope:
- Sources reviewed: `docs/COLLECTORS.md`, `docs/EXTRACTOR.md`, and collector/aggregator code under `src/SignalCollection/Collectors` and `src/FeatureExtraction/SignalAggregator`.
- Focus: signals that feed windowed feature extraction in `FeatureExtractorService`.

## Summary

Signals used by aggregators:
- Application usage: `ForegroundAppChanged`, `AppDwell`, `AppSwitchRate`
- Session state: `SessionLock`, `SessionUnlock`, `IdleSample`, `ScreenSaverOn`, `ScreenSaverOff`, `DisplayOn`, `DisplayOff`, `DisplayDimmed`
- Network context: `VpnStateChanged`, `WifiLinkChanged`, `WifiSsidChanged`, `LocalNetworkChanged`, `PublicIpBucketChanged`
- System resources: `SystemResourceTick`

Signals defined but not used by current aggregators:
- `Heartbeat`
- `WifiSsidHash` (legacy enum value)
- `Unknown`

## Windowing And Time Slices (Implementation Detail)

This section explains how the extractor turns raw event timestamps into fixed feature windows.

### Canonical Window Shape

- Windowing is profile-aware (`W60_S30`, `W120_S60`, `W30_S15`).
- Windows are computed in event time per selected profile.
- Windows are half-open intervals: `[start, end)`.
- Adjacent windows overlap by 30 seconds.

Example timeline:
- Window A: `[10:00:00, 10:01:00)`
- Window B: `[10:00:30, 10:01:30)`
- Window C: `[10:01:00, 10:02:00)`

### Epoch Alignment Rule

- Window starts are aligned to Unix epoch multiples of the step (`30s`).
- `AlignToStepUtc` floors the event timestamp to the nearest lower step boundary.

In formula form:

$$
alignedMs = tsMs - (tsMs \bmod stepMs)
$$

where:
- $tsMs$ is event timestamp in Unix milliseconds
- $stepMs = 30000$

### Event-Time (Not Processing-Time) Behavior

- The extractor uses `BroadcastSignal.TimestampUtc` as event time.
- Window completion is driven by the maximum seen event timestamp (watermark-like behavior), not wall clock.
- Late/out-of-order arrivals are inserted in timestamp order before extraction decisions.

Practical effect:
- If ingestion pauses, no new completed windows are emitted.
- If delayed events arrive with older timestamps, they can still influence not-yet-emitted windows.

### When A Window Is Considered Complete

- Let `maxEventTs` be the largest event-time observed so far.
- The latest start eligible for emission is:

$$
maxCompleteStart = align(maxEventTs - windowSize)
$$

- The extractor emits all window starts from `nextWindowStart` through `maxCompleteStart` (inclusive), stepping by 30 seconds.

This guarantees full 60-second coverage for each emitted window under event-time semantics.

### Initial Start And On-Demand Extraction Bounds

Live mode initialization:
- On first event at aligned time `Talign`, `nextWindowStart` is set to `Talign - 30s`.
- This seeds overlap-friendly progression from the first observed boundary.

On-demand file extraction:
- `firstStart = align(minEventTs) - 30s`
- `lastCompleteStart = align(maxEventTs - 60s)`
- The extractor iterates all starts in `[firstStart, lastCompleteStart]` with 30s step.

### Context Slice Used Per Window

For each target window `[Wstart, Wend)`, the extractor builds context from:
- `historyStart = Wstart - (window + step)`
- Included events satisfy: `[historyStart, Wend)`

With current constants, each window has up to 90 seconds of context (`60 + 30`) before its end.

Why this matters:
- Some aggregators reconstruct state at `Wstart` using events before the window.
- This avoids resetting states (lock/display/network status) at each boundary.

### Time Slices Inside Aggregators

Session/network aggregators:
- Rebuild state at `window.StartUtc` by replaying earlier events.
- Convert event stream into piecewise-constant intervals (time slices).
- Compute ratios by integrating slice duration over total window duration.

App aggregator:
- Uses `AppDwell.durationMs` to infer dwell segment `[eventTs - duration, eventTs)`.
- Clips each dwell segment to the target window via overlap math.
- Accumulates per-app/per-category milliseconds from clipped slices.

Cross aggregator:
- Builds `active_work` intervals from session slices.
- Intersects app overlap slices with active-work slices using overlap duration.

### Overlap Math

All clipping/intersection uses the same rule:

$$
overlapMs = \max(0, \min(a_{end}, b_{end}) - \max(a_{start}, b_{start}))
$$

Used in:
- `SlidingWindowing.SplitSegmentAcrossWindows`
- `SlidingWindowing.OverlapMs`
- `CrossFeatureAggregator` active-work intersections

### Buffer Compaction And State Carryover

To limit memory while preserving correctness, old events are compacted:
- Events newer than cutoff are kept.
- For selected stateful types, the latest event before cutoff is also retained.

State-preserved types include:
- Session: lock/unlock, display on/off/dim, screensaver on/off, idle sample
- Network: vpn, wifi link/ssid, public IP bucket

This enables correct state reconstruction at future window starts without storing the full history.

### Edge Cases To Expect

- Empty window context: no row emitted unless the window is considered complete and has context during extraction path.
- Missing payload keys: aggregators default to safe values (mostly zero/false).
- Negative/invalid durations: clamped or ignored (for example, non-positive app dwell durations are skipped).
- GPU unavailable: system features still emitted with GPU metrics zeroed and availability ratios reflecting missing data.

## Signal -> Aggregator Mapping

## ApplicationUsageCollector Signals

### ForegroundAppChanged
Emitted payload keys:
- `appKey`
- `category`
- `collectorMode`
- `confidence`

Consumed by:
- `AppFeatureAggregator`

Payload keys read by aggregators:
- `collectorMode` (used for `collector_mode_hook_ratio`)

Features affected:
- `collector_mode_hook_ratio`
- `app_switch_count` (fallback mode: count of `ForegroundAppChanged` when no valid `AppSwitchRate` in window)

### AppDwell
Emitted payload keys:
- `appKey`
- `category`
- `durationMs`
- `reason`
- `dwellReason`
- `collectorMode`
- `confidence`

Consumed by:
- `AppFeatureAggregator`

Payload keys read by aggregators:
- `durationMs`
- `appKey`
- `category`
- `confidence`
- `reason` (with fallback to `dwellReason`)

Features affected:
- `has_app_data`
- `app_unique_count`
- `app_dwell_mean_ms`
- `app_dwell_std_ms`
- `app_dwell_max_ms`
- `app_top1_share`
- `cat_browser_ratio`
- `cat_ide_ratio`
- `cat_terminal_ratio`
- `cat_comms_ratio`
- `cat_office_ratio`
- `cat_media_ratio`
- `cat_design_ratio`
- `cat_database_ratio`
- `cat_gaming_ratio`
- `cat_remoteaccess_ratio`
- `cat_filemanager_ratio`
- `cat_email_ratio`
- `cat_system_ratio`
- `cat_other_ratio`
- `app_confidence_high_ratio`
- `no_foreground_end_count`

### AppSwitchRate
Emitted payload keys:
- `windowSec`
- `switches`
- `collectorMode`

Consumed by:
- `AppFeatureAggregator`

Payload keys read by aggregators:
- `switches`

Features affected:
- `app_switch_count` (preferred source)

## SessionStateCollector Signals

### SessionLock
Emitted payload keys:
- `source`
- `reason`

Consumed by:
- `SessionFeatureAggregator`

Payload keys read by aggregators:
- none (type only)

Features affected:
- `lock_count`
- `locked_ratio`

### SessionUnlock
Emitted payload keys:
- `source`
- `reason`

Consumed by:
- `SessionFeatureAggregator`

Payload keys read by aggregators:
- none (type only)

Features affected:
- `unlock_count`
- `locked_ratio`

### IdleSample
Emitted payload keys (union of paths):
- `idleMs`
- `idleBucketSec`
- `idleStatus`
- `userPresence` (optional)
- `presenceSource` (optional)
- `screensaverStatus` (optional)

Consumed by:
- `SessionFeatureAggregator`

Payload keys read by aggregators:
- `idleStatus`
- `idleBucketSec`
- `userPresence`

Features affected:
- `idle_api_fail_count`
- `idle_bucket_mean_sec`
- `idle_bucket_max_sec`
- `idle_ge_60_ratio`
- `idle_ge_300_ratio`
- `has_idle_data`
- `presence_away_ratio`
- `presence_present_ratio`
- `presence_available_ratio`

### ScreenSaverOn / ScreenSaverOff
Emitted payload keys:
- `running`
- `screensaverStatus`

Consumed by:
- `SessionFeatureAggregator`

Payload keys read by aggregators:
- none (type only)

Features affected:
- `screensaver_toggle_count`
- `screensaver_on_ratio`

### DisplayOn / DisplayOff / DisplayDimmed
Emitted payload keys:
- `displayState`
- `source`
- `confidence`
- `userPresence` (optional)
- `presenceSource` (optional)

Consumed by:
- `SessionFeatureAggregator`

Payload keys read by aggregators:
- `userPresence` (optional)

Features affected:
- `display_toggle_count`
- `display_on_ratio`
- `display_off_ratio`
- `display_dim_ratio`
- `has_display_data`
- `presence_away_ratio`
- `presence_present_ratio`
- `presence_available_ratio`

## NetworkContextCollector Signals

### VpnStateChanged
Emitted payload keys:
- `vpnOn`
- `vpnAdapter`
- `vpnConfidence`
- `vpnReason`
- `initial` (initial snapshot only)

Consumed by:
- `NetworkFeatureAggregator`

Payload keys read by aggregators:
- `vpnOn`

Features affected:
- `vpn_flip_count`
- `vpn_on_ratio`

### WifiLinkChanged
Emitted payload keys:
- `wifiUp`
- `wifiIdentityConfidence`
- `wifiIdentityReason`
- `initial` (initial snapshot only)

Consumed by:
- `NetworkFeatureAggregator`

Payload keys read by aggregators:
- `wifiUp`

Features affected:
- `wifi_flip_count`
- `primary_wifi_connected_ratio`
- `wifi_up_ratio` (compatibility alias)

### WifiSsidChanged
Emitted payload keys:
- `wifiSsid`
- `wifiUp`
- `wifiBssidHash`
- `wifiIdentityConfidence`
- `wifiIdentityReason`
- `initial` (initial snapshot only)

Consumed by:
- `NetworkFeatureAggregator`

Payload keys read by aggregators:
- `wifiSsid`
- `wifiBssidHash`
- `wifiUp`

Features affected:
- `ssid_change_count`
- `unique_wifi_ssid_count`
- `unique_wifi_bssid_count`
- `primary_wifi_connected_ratio`
- `wifi_up_ratio` (compatibility alias)

### LocalNetworkChanged
Emitted payload keys:
- `localPrefix`
- `localNetworkHash`
- `localIpFamily`
- `localPrefixHash`
- `localNetworkReason`
- `initial` (initial snapshot only)

Consumed by:
- `NetworkFeatureAggregator`

Payload keys read by aggregators:
- `localNetworkHash`

Features affected:
- `local_network_change_count`
- `local_prefix_change_count` (compatibility alias)
- `unique_local_network_count`

### PublicIpBucketChanged
Emitted payload keys:
- `publicIpBucket`
- `publicIpAgeSeconds`
- `publicIpFetchStatus`
- `initial` (initial snapshot only)

Consumed by:
- `NetworkFeatureAggregator`

Payload keys read by aggregators:
- `publicIpBucket`
- `publicIpFetchStatus`

Features affected:
- `public_ip_bucket_change_count`
- `unique_public_bucket_count`
- `public_ip_fetch_fail_count`
- `public_ip_known_ratio`
- `public_ip_backoff_ratio`

## SystemResourceCollector Signals

### SystemResourceTick
Emitted payload keys:
- `cpu_available`
- `cpu_pct`
- `mem_available`
- `mem_used_pct`
- `mem_avail_mb`
- `mem_total_mb`
- `gpu_available`
- `gpu_pct`
- `gpu_mem_used_pct`
- `gpu_engine_active_count`
- `swap_available`
- `net_rx_kbps`
- `net_tx_kbps`

Consumed by:
- `SystemResourceFeatureAggregator`

Payload keys read by aggregators:
- `cpu_available`, `cpu_pct`
- `mem_available`, `mem_used_pct`
- `gpu_available`, `gpu_pct`, `gpu_mem_used_pct`
- `net_tx_kbps`, `net_rx_kbps`

Features affected:
- `cpu_usage_mean`
- `cpu_usage_max`
- `cpu_usage_std`
- `cpu_usage_high_ratio`
- `cpu_spike_count`
- `ram_usage_mean`
- `ram_usage_max`
- `ram_usage_std`
- `ram_high_usage_ratio`
- `ram_pressure_events`
- `gpu_available`
- `gpu_usage_mean`
- `gpu_usage_max`
- `gpu_usage_std`
- `gpu_memory_usage_mean`
- `gpu_high_usage_ratio`
- `cpu_data_available_ratio`
- `mem_data_available_ratio`
- `gpu_data_available_ratio`
- `net_tx_kbps_mean`
- `net_rx_kbps_mean`
- `net_total_kbps_mean`
- `net_total_kbps_max`
- `net_bytes_sent_mean` (compatibility alias)
- `net_bytes_recv_mean` (compatibility alias)
- `net_bytes_total_mean` (compatibility alias)
- `net_bytes_total_max` (compatibility alias)
- `net_activity_ratio`
- `net_throughput_std`
- `net_spike_count`
- `system_load_index`
- `resource_variability_index`
- `cpu_ram_correlation_proxy`
- `active_resource_ratio`
- `has_system_data`

Removed collector-side summary keys (not consumed anymore):
- `window_sec`
- `cpu_mean_pct`, `cpu_std_pct`, `cpu_max_pct`, `cpu_high_ratio`, `cpu_idle_ratio`, `cpu_spike_count`, `cpu_bucket_flip_count`
- `mem_mean_used_pct`, `mem_std_used_pct`, `mem_pressure_ratio`, `mem_available_bucket`, `mem_swap_activity`, `mem_range_pct`
- `gpu_mean_pct`, `gpu_active_ratio`, `gpu_spike_count`, `gpu_bucket_flip_count`
- `net_rx_mean_kbps`, `net_rx_std_kbps`, `net_tx_mean_kbps`, `net_tx_std_kbps`, `net_upload_ratio`

