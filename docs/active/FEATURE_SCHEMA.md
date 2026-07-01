# Feature Extraction Schema (v1.2.2)

## Windowing Rules
- Event-time windowing: use `SignalEvent.ts` (broadcast timestamp), never processing time.
- Fixed windows: `window_sec = 60`, `step_sec = 30`.
- Alignment: window starts are aligned to Unix epoch multiples of 30 seconds.
- Window interval: `[window_start_ts, window_start_ts + 60s)`.
- Segment overlap: `overlap_ms = max(0, min(seg_end, win_end) - max(seg_start, win_start))`.

## Source Separation Rules
- Session availability/presence features: only SessionStateCollector signals.
- Focus/task-switching features: only ApplicationUsageCollector signals.
- Environment/network features: only NetworkContextCollector signals.
- System resource features: only SystemResourceCollector signals.
- Cross features are explicitly gated by session-derived `active_work` intervals.

## app_window_features (ApplicationUsageCollector only)

Signals: `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate`, `AppFocusHeartbeat`.
The aggregator builds overlap segments — the portion of each `AppDwell` segment [start, end, appKey, category] that intersects the window — and counts discrete point events inside the window. If the latest `AppFocusHeartbeat` in context has no closing `AppDwell`, the aggregator synthesizes an open-dwell segment `[dwellStartUtc, window.EndUtc)` for the current foreground app and clips it to the window.

| Feature | Signal(s) | Computation |
|---|---|---|
| `app_switch_count` | `AppSwitchRate`, `ForegroundAppChanged` | Prefers `AppSwitchRate.switches` from the latest rate event in the window; falls back to count of `ForegroundAppChanged` events |
| `app_unique_count` | `AppDwell` | Count of distinct `appKey` values across all overlap segments |
| `app_dwell_mean_ms` | `AppDwell` | Mean overlap duration in ms across all segments |
| `app_dwell_std_ms` | `AppDwell` | Std dev of overlap durations in ms |
| `app_dwell_max_ms` | `AppDwell` | Max single-segment overlap duration in ms |
| `app_top1_share` | `AppDwell` | (total ms for the single most-used app) / (total dwell ms) — 1.0 means only one app used |
| `cat_browser_ratio` | `AppDwell` | (overlap ms where category=`browser`) / window_ms |
| `cat_ide_ratio` | `AppDwell` | (overlap ms where category=`ide`) / window_ms |
| `cat_terminal_ratio` | `AppDwell` | (overlap ms where category=`terminal`) / window_ms |
| `cat_comms_ratio` | `AppDwell` | (overlap ms where category=`comms`) / window_ms |
| `cat_office_ratio` | `AppDwell` | (overlap ms where category=`office`) / window_ms |
| `cat_media_ratio` | `AppDwell` | (overlap ms where category=`media`) / window_ms |
| `cat_design_ratio` | `AppDwell` | (overlap ms where category=`design`) / window_ms |
| `cat_database_ratio` | `AppDwell` | (overlap ms where category=`database`) / window_ms |
| `cat_gaming_ratio` | `AppDwell` | (overlap ms where category=`gaming`) / window_ms |
| `cat_remoteaccess_ratio` | `AppDwell` | (overlap ms where category=`remoteaccess`) / window_ms |
| `cat_filemanager_ratio` | `AppDwell` | (overlap ms where category=`filemanager`) / window_ms |
| `cat_email_ratio` | `AppDwell` | (overlap ms where category=`email`) / window_ms |
| `cat_system_ratio` | `AppDwell` | (overlap ms where category=`system`) / window_ms |
| `cat_other_ratio` | `AppDwell` | (overlap ms where category=`other` or unrecognized) / window_ms |
| `app_confidence_high_ratio` | `AppDwell` | (overlap ms where `confidence="high"`) / total dwell ms — measures how reliably apps were identified |
| `no_foreground_end_count` | `AppDwell` | Count of `AppDwell` events with `reason="no_foreground"` — dwell ended because no window had focus |
| `collector_mode_hook_ratio` | `ForegroundAppChanged` | (events with `collectorMode="hook"`) / total `ForegroundAppChanged` — fraction detected by OS hook vs polling |
| `has_app_data` | `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate` | 1.0 if any overlap segments or window events exist; 0.0 otherwise |

## session_window_features (SessionStateCollector only)

Signals: `SessionLock`, `SessionUnlock`, `DisplayOn`, `DisplayOff`, `DisplayDimmed`, `ScreenSaverOn`, `ScreenSaverOff`, `IdleSample`.
The aggregator replays a state machine from all history before the window to establish initial state, then applies transitions inside the window to produce time-weighted sub-intervals. Each interval carries: `Locked`, `DisplayState` (On/Off/Dimmed/Unknown), `ScreenSaverOn`, `PresenceKnown/Away`, `IdleKnown/IdleBucketSec`. Presence is updated by the `userPresence` payload field on `DisplayOn/Off/Dimmed` and `IdleSample` events.

| Feature | Signal(s) | Computation |
|---|---|---|
| `lock_count` | `SessionLock` | Count of `SessionLock` events inside the window |
| `unlock_count` | `SessionUnlock` | Count of `SessionUnlock` events inside the window |
| `display_toggle_count` | `DisplayOn`, `DisplayOff`, `DisplayDimmed` | Incremented whenever `DisplayState` actually changes on one of these events |
| `screensaver_toggle_count` | `ScreenSaverOn`, `ScreenSaverOff` | Incremented whenever `ScreenSaverOn` flag flips |
| `locked_ratio` | `SessionLock`, `SessionUnlock` | locked_ms / window_ms — time-weighted from state machine intervals |
| `display_off_ratio` | `DisplayOff` | display_off_ms / window_ms |
| `display_dim_ratio` | `DisplayDimmed` | display_dim_ms / window_ms |
| `display_on_ratio` | `DisplayOn` | display_on_ms / window_ms |
| `screensaver_on_ratio` | `ScreenSaverOn`, `ScreenSaverOff` | screensaver_ms / window_ms |
| `presence_away_ratio` | `IdleSample`, `DisplayOn/Off/Dimmed` (via `userPresence` payload) | presence_away_ms / window_ms — only intervals where presence is known count toward away |
| `presence_present_ratio` | same | presence_present_ms / window_ms |
| `presence_available_ratio` | same | presence_known_ms / window_ms — data quality: fraction of window where presence was known at all |
| `idle_bucket_mean_sec` | `IdleSample` | Time-weighted mean: Σ(idleBucketSec × interval_ms) / total_known_ms — only intervals with known idle |
| `idle_bucket_max_sec` | `IdleSample` | Max `idleBucketSec` seen across known idle intervals |
| `idle_ge_60_ratio` | `IdleSample` | (ms where idleBucketSec ≥ 60) / window_ms |
| `idle_ge_300_ratio` | `IdleSample` | (ms where idleBucketSec ≥ 300) / window_ms — user away ≥ 5 min |
| `has_idle_data` | `IdleSample` | 1.0 if any interval had known idle state |
| `has_display_data` | `DisplayOn`, `DisplayOff`, `DisplayDimmed` | 1.0 if any display On/Off/Dim milliseconds were recorded |
| `idle_api_fail_count` | `IdleSample` | Count of `IdleSample` events with `idleStatus="api_fail"` — OS idle API was unavailable |

## network_window_features (NetworkContextCollector only)

Signals: `VpnStateChanged`, `WifiLinkChanged`, `WifiSsidChanged`, `LocalNetworkChanged`, `PublicIpBucketChanged`.
Like session, uses a state machine seeded from history before the window to produce time-weighted durations. Point events are also counted directly inside the window.

| Feature | Signal(s) | Computation |
|---|---|---|
| `vpn_on_ratio` | `VpnStateChanged` | vpn_on_ms / window_ms — state toggled by `VpnStateChanged.vpnOn` |
| `primary_wifi_connected_ratio` | `WifiLinkChanged`, `WifiSsidChanged` | wifi_up_ms / window_ms — state toggled by `wifiUp` payload field |
| `public_ip_known_ratio` | `PublicIpBucketChanged` | public_known_ms / window_ms — requires `publicIpFetchStatus="ok"` and a non-empty, non-`"none"` bucket |
| `vpn_flip_count` | `VpnStateChanged` | Count of `VpnStateChanged` events in window |
| `wifi_flip_count` | `WifiLinkChanged` | Count of `WifiLinkChanged` events in window |
| `ssid_change_count` | `WifiSsidChanged` | Count of times `wifiSsid` differed from the previous `WifiSsidChanged` value |
| `local_network_change_count` | `LocalNetworkChanged` | Count of `LocalNetworkChanged` events in window |
| `public_ip_bucket_change_count` | `PublicIpBucketChanged` | Count of `PublicIpBucketChanged` events in window |
| `unique_wifi_ssid_count` | `WifiSsidChanged` | Distinct `wifiSsid` values seen in window (excludes `"none"` / `"unknown"`) |
| `unique_wifi_bssid_count` | `WifiSsidChanged` | Distinct `wifiBssidHash` values seen in window |
| `unique_local_network_count` | `LocalNetworkChanged` | Distinct `localNetworkHash` values seen in window |
| `unique_public_bucket_count` | `PublicIpBucketChanged` | Distinct `publicIpBucket` values seen in window |
| `public_ip_fetch_fail_count` | `PublicIpBucketChanged` | Count of events with `publicIpFetchStatus="fail"` |
| `public_ip_backoff_ratio` | `PublicIpBucketChanged` | backoff_ms / window_ms — state `PublicIpBackoff=true` when status is `"backoff"` |
| `has_net_data` | all network signals | 1.0 if any network events fell in the window |
| `wifi_up_ratio` | — | Alias of `primary_wifi_connected_ratio` (v1 compat) |
| `local_prefix_change_count` | — | Alias of `local_network_change_count` (v1 compat) |

## cross_window_features (gated)

Sources: session state machine intervals × app overlap segments. No new signals; this layer re-uses the outputs of `SessionFeatureAggregator` and `AppFeatureAggregator`.

Active-work intervals are sub-intervals from the session state machine where all of the following hold:

```
active_work := !Locked
           AND DisplayState == On
           AND (PresenceKnown → !PresenceAway)
           AND (IdleKnown    → IdleBucketSec < 300)
```

| Feature | Computation |
|---|---|
| `active_work_ratio` | active_ms / window_ms — fraction of window where user was genuinely at the machine |
| `app_switches_per_active_min` | app_switch_count / active_minutes — switching rate normalised to active time only; 0 if no active time |
| `category_entropy_active` | Shannon entropy over per-category share of dwell time that overlaps active intervals — high = diverse app usage, low = focused on one category |

## system_window_features (SystemResourceCollector only)

Signal: `SystemResourceTick` — a periodic sample with payload fields `cpu_pct`, `mem_used_pct`, `gpu_pct`, `gpu_mem_used_pct`, `net_tx_kbps`, `net_rx_kbps`, and availability flags `cpu_available`, `mem_available`, `gpu_available`. CPU and RAM values are only included in stats when their `*_available` flag is `true`.

### CPU (`cpu_pct`, requires `cpu_available=true`)
| Feature | Computation |
|---|---|
| `cpu_usage_mean` | Mean CPU % across all available ticks |
| `cpu_usage_max` | Max CPU % |
| `cpu_usage_std` | Std dev of CPU % |
| `cpu_usage_high_ratio` | Fraction of ticks where CPU % > 80 |
| `cpu_spike_count` | Count of consecutive-tick increases ≥ 20 % |

### RAM (`mem_used_pct`, requires `mem_available=true`)
| Feature | Computation |
|---|---|
| `ram_usage_mean` | Mean RAM used % |
| `ram_usage_max` | Max RAM used % |
| `ram_usage_std` | Std dev of RAM % |
| `ram_high_usage_ratio` | Fraction of ticks where RAM > 85 % |
| `ram_pressure_events` | Count of times RAM crossed the 85 % threshold from below |

### GPU (`gpu_pct`, `gpu_mem_used_pct`, requires `gpu_available=true`)
| Feature | Computation |
|---|---|
| `gpu_available` | 1.0 if any tick had `gpu_available=true`; 0.0 otherwise |
| `gpu_usage_mean` | Mean GPU utilization % |
| `gpu_usage_max` | Max GPU utilization % |
| `gpu_usage_std` | Std dev of GPU % |
| `gpu_memory_usage_mean` | Mean GPU memory used % |
| `gpu_high_usage_ratio` | Fraction of ticks where GPU > 70 % |

### Network I/O (`net_tx_kbps`, `net_rx_kbps`, always present)
| Feature | Computation |
|---|---|
| `net_bytes_sent_mean` / `net_tx_kbps_mean` | Mean upload kbps |
| `net_bytes_recv_mean` / `net_rx_kbps_mean` | Mean download kbps |
| `net_bytes_total_mean` / `net_total_kbps_mean` | Mean of (tx + rx) kbps |
| `net_bytes_total_max` / `net_total_kbps_max` | Max of (tx + rx) kbps |
| `net_activity_ratio` | Fraction of ticks where total throughput > 1 kbps |
| `net_throughput_std` | Std dev of total kbps |
| `net_spike_count` | Count of consecutive-tick increases ≥ 1000 kbps |

### Composite indices
| Feature | Formula |
|---|---|
| `system_load_index` | With GPU: `cpu_mean×0.4 + ram_mean×0.35 + gpu_mean×0.25`; without GPU: `cpu_mean×0.55 + ram_mean×0.45` |
| `resource_variability_index` | Mean of `(cpu_std, ram_std, net_throughput_std, [gpu_std if available])` |
| `cpu_ram_correlation_proxy` | `abs(cpu_usage_mean − ram_usage_mean)` — large value means CPU and RAM diverge (potential anomaly signal) |
| `active_resource_ratio` | Mean of `(cpu_high_ratio, net_activity_ratio, [gpu_high_ratio if available])` |
| `has_system_data` | Always 1.0 when any `SystemResourceTick` events were in the window |

## quality_window_features (v1.2.1+)

Not tied to a collector signal — derived from `PowerSuspend`/`PowerResume`/`CollectionGapDetected` events observed by `FeatureExtractorService`, independent of any single collector's aggregator.

| Feature | Computation |
|---|---|
| `has_collection_gap` | `1.0` if the window overlaps a detected `CollectionGapDetected` interval (sleep/hibernate); `0.0` otherwise. Defaults to `0.0` on the historical replay / on-demand extraction path. |
| `in_warm_up` | `1.0` if `window_start_ts` falls within `WarmUpAfterResumeSec` (default 30s) seconds after a `PowerResume` event; `0.0` otherwise. Defaults to `0.0` on the historical replay / on-demand extraction path. |

Scoring consumers should filter: `WHERE has_collection_gap = 0 AND in_warm_up = 0`.

## Data Quality
Missingness is represented explicitly by:
- `has_app_data`, `has_idle_data`, `has_display_data`, `has_net_data`
- `presence_available_ratio`
- API/status quality counts and ratios (`idle_api_fail_count`, `public_ip_fetch_fail_count`, `public_ip_backoff_ratio`)
- Sleep/resume transition quality (`has_collection_gap`, `in_warm_up`) — see `quality_window_features` above

## v1 Compatibility
Legacy columns are still present/compatible:
- `cat_browser_ratio`, `cat_ide_ratio`, `cat_comms_ratio`, `cat_other_ratio`
- `lock_count`, `locked_ratio`, `display_off_ratio`, `screensaver_on_ratio`
- `idle_bucket_mean_sec`, `idle_bucket_max_sec`, `idle_ge_60_ratio`
- `vpn_on_ratio`, `wifi_up_ratio` (alias of `primary_wifi_connected_ratio`)
- `local_prefix_change_count` (alias of `local_network_change_count`)
