# Behavioral Feature Analysis — Continuous Authentication

---

## How Features Are Produced

Each row in `feature_rows` covers one time window (W30_S15 = 30 s window, 15 s slide; W60_S30 = 60 s/30 s; W120_S60 = 120 s/60 s). Features are extracted from raw signals using four domain aggregators plus a cross-domain aggregator:

```
Raw signals (spool/raw_signals.jsonl)
    └── AppFeatureAggregator      →  app_*, cat_*, has_app_data
    └── SessionFeatureAggregator  →  lock_*, idle_*, display_*, presence_*, screensaver_*
    └── NetworkFeatureAggregator  →  vpn_*, wifi_*, ssid_*, public_ip_*, local_network_*
    └── SystemResourceAggregator  →  cpu_*, ram_*, gpu_*, net_bytes_*, system_load_*, …
    └── CrossFeatureAggregator    →  active_work_ratio, app_switches_per_active_min, category_entropy_active
```

State-machine aggregators (Session, Network) replay history before each window to reconstruct current state, then integrate time-weighted sub-intervals. App aggregator clips `AppDwell` segments by overlap with the window. `AppFocusHeartbeat` synthesizes an open-dwell segment for the current foreground app if it has not yet closed.

All features live inside `features_json` (JSON string). The `source_counts_json` column records how many raw signals from each domain fell in the window.

---

## Domain 1 — App Features (ApplicationUsageCollector)

**Signals:** `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate`, `AppFocusHeartbeat`

Every `AppDwell` event says "I was in app X for Y ms, then switched". The aggregator clips each dwell by overlap with the window (overlap math: `max(0, min(seg_end,win_end) - max(seg_start,win_start))`). `AppFocusHeartbeat` fires every 15 s during sustained focus and lets the aggregator synthesize a running open-dwell segment so windows mid-dwell still have app coverage.

### Feature Descriptions

| Feature | What it measures | Unit / Range |
|---------|-----------------|--------------|
| `app_switch_count` | How many times the foreground app changed in this window. Prefers `AppSwitchRate.switches` (60 s pre-aggregated count); falls back to counting `ForegroundAppChanged` events. | Count, 0–~20 per 30 s window |
| `app_unique_count` | Distinct apps (by `appKey` hash) whose dwell overlapped this window. | Count, 1–9 observed |
| `app_dwell_mean_ms` | Average overlap duration per app dwell segment in ms. A user who switches rapidly has low mean; a user who stays in one app has high mean (capped at window_ms = 30 000 ms for W30_S15). | ms, 0–30 000 |
| `app_dwell_std_ms` | Std dev of overlap durations — high = mixed short-and-long dwells, low = consistent dwell lengths. | ms |
| `app_dwell_max_ms` | The single longest dwell overlap in the window. Usually equals window_ms (30 000) when the user stays in one app the entire window. | ms, 0–30 000 |
| `app_top1_share` | Fraction of total dwell time spent in the single most-used app this window. 1.0 = pure single-app. 0.5 = two apps equally split. | 0.0–1.0 |
| `cat_browser_ratio` | Fraction of window_ms spent in browser-category apps (Chrome, Firefox, Edge, etc.). | 0.0–1.0 |
| `cat_ide_ratio` | Fraction of window_ms in IDE apps (VS Code, Visual Studio, IntelliJ, etc.). | 0.0–1.0 |
| `cat_terminal_ratio` | Fraction of window_ms in terminal/shell apps (Windows Terminal, cmd, PowerShell, etc.). | 0.0–1.0 |
| `cat_comms_ratio` | Fraction of window_ms in communication apps (Teams, Slack, Discord, Outlook, etc.). | 0.0–1.0 |
| `cat_office_ratio` | Fraction of window_ms in office productivity apps (Word, Excel, PowerPoint, etc.). | 0.0–1.0 |
| `cat_media_ratio` | Fraction of window_ms in media apps (VLC, Spotify, Windows Media Player, etc.). | 0.0–1.0 |
| `cat_design_ratio` | Fraction of window_ms in design/art apps (Photoshop, Figma, Illustrator, etc.). | 0.0–1.0 |
| `cat_database_ratio` | Fraction of window_ms in database tools (DBeaver, SSMS, pgAdmin, etc.). | 0.0–1.0 |
| `cat_gaming_ratio` | Fraction of window_ms in games (Steam games, etc.). | 0.0–1.0 |
| `cat_remoteaccess_ratio` | Fraction of window_ms in remote desktop tools (RDP, TeamViewer, AnyDesk, etc.). | 0.0–1.0 |
| `cat_filemanager_ratio` | Fraction of window_ms in file manager apps (Explorer, Total Commander, etc.). | 0.0–1.0 |
| `cat_email_ratio` | Fraction of window_ms in dedicated email clients (Outlook standalone, Thunderbird, etc.). | 0.0–1.0 |
| `cat_system_ratio` | Fraction of window_ms in system/admin tools (Task Manager, Registry Editor, Control Panel, etc.). | 0.0–1.0 |
| `cat_other_ratio` | Fraction of window_ms in apps that don't match any above category (unrecognized exes, custom tools, etc.). | 0.0–1.0 |
| `app_confidence_high_ratio` | Fraction of dwell ms where the app was identified with high confidence (full executable path resolved vs. name-only fallback). | 0.0–1.0 |
| `no_foreground_end_count` | Count of `AppDwell` events that ended because no window had foreground focus (reason=`no_foreground`). Captures machine-active-but-no-focused-app moments. | Count, 0–n |
| `collector_mode_hook_ratio` | Fraction of `ForegroundAppChanged` events detected via OS hook vs. polling fallback. Hook mode is more precise (sub-second latency). | 0.0–1.0 |
| `has_app_data` | 1.0 if any app overlap segment or event exists; 0.0 if the window had no app signal at all. Key quality gate. | 0 or 1 |

### Observed Distributions — Mean (W30_S15, 1 000 active-app rows each, all 15 new replayed DBs)

> `app_switches_per_active_min` P009 outlier (2 325) is a single window artifact; treat as noise.

| Feature | P001 | P002 | P003 | N004 | P005 | P006 | P007 | P008 | P009 | P010 | P011 | P012 | P013 | P014 | P015 |
|---------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|
| `app_switch_count` | 2.8 | 4.0 | 4.8 | 3.9 | 2.4 | 2.7 | 2.4 | 5.3 | 3.8 | 3.5 | 2.3 | 4.0 | 3.8 | 2.3 | 4.1 |
| `app_unique_count` | 1.2 | 1.3 | 1.5 | 1.5 | 1.2 | 1.1 | 1.1 | 1.3 | 1.7 | 1.3 | 1.1 | 1.1 | 1.6 | 1.1 | 1.4 |
| `app_dwell_mean_ms` | 27 666 | 25 606 | 24 501 | 23 187 | 27 828 | 28 961 | 28 487 | 25 608 | 22 466 | 25 000 | 28 481 | 28 968 | 22 530 | 29 058 | 25 051 |
| `app_top1_share` | 0.97 | 0.95 | 0.94 | 0.93 | 0.97 | 0.99 | 0.99 | 0.95 | 0.91 | 0.96 | 0.98 | 0.99 | 0.91 | 0.99 | 0.94 |
| `cat_browser_ratio` | 0.84 | 0.91 | 0.71 | 0.69 | 0.89 | 0.88 | **0.98** | 0.85 | 0.83 | 0.33 | 0.08 | **0** | 0.58 | 0.61 | 0.73 |
| `cat_ide_ratio` | **0** | 0.55 | 0.46 | **0** | 0.44 | **0** | **0** | **0** | 0.51 | **0** | **0.90** | **0.81** | **0.70** | **0** | 0.13 |
| `cat_terminal_ratio` | ~0 | 0.07 | **0.36** | **0** | **0** | **0.78** | **0** | ~0 | **0.35** | **0** | **0.91** | **0.31** | 0.20 | 0.23 | 0.14 |
| `cat_comms_ratio` | 0.37 | **0** | 0.35 | **0.88** | 0.33 | **0** | **0** | 0.16 | 0.12 | 0.18 | **0** | 0.17 | **0.54** | **0** | 0.21 |
| `cat_office_ratio` | **0** | **0** | ~0 | **0** | **0** | **0** | **0** | **0.54** | **0** | **0** | **0** | **0** | **0.58** | **0** | **0** |
| `cat_media_ratio` | 0 | 0 | 0 | 0 | **0.99** | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `cat_gaming_ratio` | 0 | 0 | 0 | 0 | 0.07 | 0 | 0 | 0.19 | 0 | 0 | 0 | 0 | 0 | 0 | 0.04 |
| `cat_other_ratio` | 0.94 | 0.74 | 0.86 | 0.73 | 0.34 | **0.98** | 0.32 | 0.87 | 0.71 | **0.92** | **0.96** | **0.98** | 0.92 | **0.98** | 0.86 |

### Nonzero % (fraction of windows where feature > 0)

| Feature | P001 | P002 | P003 | N004 | P005 | P006 | P007 | P008 | P009 | P010 | P011 | P012 | P013 | P014 | P015 |
|---------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|
| `cat_browser_ratio` | 31% | **84%** | 28% | 49% | 40% | 4% | **95%** | 65% | 55% | 21% | 0.2% | 0% | 30% | 3% | 29% |
| `cat_ide_ratio` | 0% | 17% | 3% | 0% | 1% | 0% | 0% | 0% | 11% | 0% | **28%** | 2% | **36%** | 0% | 0.2% |
| `cat_terminal_ratio` | 0.2% | 0.4% | **7%** | 0% | 0% | 1% | 0% | 0.2% | 5% | 0% | 3% | 0.4% | 0.2% | 0.2% | 0.4% |
| `cat_comms_ratio` | 4% | 0% | 4% | **21%** | 2% | 0% | 0% | 0.4% | 1% | 6% | 0% | 0.1% | **18%** | 0% | 5% |
| `cat_office_ratio` | 0% | 0% | 0.2% | 0% | 0% | 0% | 0% | **10%** | 0% | 0% | 0% | 0% | 6% | 0% | 0% |
| `cat_media_ratio` | 0% | 0% | 0% | 0% | **47%** | 0% | 0% | 0% | 0% | 0% | 0% | 0% | 0% | 0% | 0% |

---

## Domain 2 — Session Features (SessionStateCollector)

**Signals:** `SessionLock`, `SessionUnlock`, `DisplayOn/Off/Dimmed`, `ScreenSaverOn/Off`, `IdleSample`

The aggregator replays a state machine from history to establish state at window start, then produces piecewise-constant intervals through the window. Ratios are always time-weighted (ms of state / window_ms). The state machine tracks: `Locked` (bool), `DisplayState` (On/Off/Dimmed/Unknown), `ScreenSaverOn` (bool), `PresenceKnown/PresenceAway` (from `IdleSample.userPresence`), `IdleKnown/IdleBucketSec`.

### Feature Descriptions

| Feature | What it measures | Unit / Range |
|---------|-----------------|--------------|
| `lock_count` | How many times the session was locked (screen locked, Win+L, etc.) in this window. | Count 0–n |
| `unlock_count` | How many times the session was unlocked in this window. | Count 0–n |
| `display_toggle_count` | How many times the display state changed (On↔Off↔Dimmed) in this window. | Count 0–n |
| `screensaver_toggle_count` | How many times screensaver toggled on/off in this window. | Count 0–n |
| `locked_ratio` | Fraction of window_ms where session was locked. 1.0 = fully locked window. | 0.0–1.0 |
| `display_off_ratio` | Fraction of window_ms where display was off. | 0.0–1.0 |
| `display_dim_ratio` | Fraction of window_ms where display was dimmed (e.g. auto-dim before sleep). | 0.0–1.0 |
| `display_on_ratio` | Fraction of window_ms where display was on (active). | 0.0–1.0 |
| `screensaver_on_ratio` | Fraction of window_ms where screensaver was running. | 0.0–1.0 |
| `presence_away_ratio` | Fraction of window_ms where system presence detection said user was away. Only meaningful when presence is known (see `presence_available_ratio`). | 0.0–1.0 |
| `presence_present_ratio` | Fraction of window_ms where presence detection said user was present. | 0.0–1.0 |
| `presence_available_ratio` | Fraction of window_ms where presence was known at all — data quality flag. Low = presence API wasn't reporting. | 0.0–1.0 |
| `idle_bucket_mean_sec` | Time-weighted mean idle bucket (last input time in discrete buckets: 0, 15, 30, 60, 120, 180, 300 s). A high value means the user was not touching the keyboard/mouse for long stretches. | Seconds, 0–300+ |
| `idle_bucket_max_sec` | Maximum idle bucket observed during this window. | Seconds, 0–1 560 observed |
| `idle_ge_60_ratio` | Fraction of window_ms where idle bucket ≥ 60 s (user inactive for at least 1 min). | 0.0–1.0 |
| `idle_ge_300_ratio` | Fraction of window_ms where idle bucket ≥ 300 s (user inactive for at least 5 min). | 0.0–1.0 |
| `has_idle_data` | 1.0 if any `IdleSample` event provided known idle state. | 0 or 1 |
| `has_display_data` | 1.0 if any display On/Off/Dim interval was recorded. | 0 or 1 |
| `idle_api_fail_count` | Count of `IdleSample` events where the OS idle API was unavailable (error path). | Count 0–n |

### Observed Distributions — all 15 participants

| Feature | P001 | P002 | P003 | N004 | P005 | P006 | P007 | P008 | P009 | P010 | P011 | P012 | P013 | P014 | P015 |
|---------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|
| `idle_bucket_mean_sec` | 54 | 96 | **3 404** | 534 | 568 | 499 | 61 | 129 | 147 | **20** | 192 | **1 212** | 502 | **3 731** | 188 |
| `idle_bucket_max_sec` | 71 | 115 | **3 415** | 545 | 590 | 523 | 78 | 149 | 164 | 33 | 210 | **1 230** | 516 | **3 746** | 205 |
| `idle_ge_300_ratio` (% nonzero) | 1% | 7% | **59%** | 15% | 28% | 47% | 2% | 8% | 14% | **0%** | 16% | **74%** | 25% | **77%** | 11% |
| `active_work_ratio` mean | 0.79 | 0.84 | 0.88 | 0.76 | 0.79 | 0.78 | 0.75 | 0.84 | 0.75 | 0.77 | 0.80 | 0.74 | 0.77 | 0.81 | 0.80 |
| `presence_present_ratio` (% nonzero) | 44% | 86% | 90% | 55% | 96% | 93% | 49% | 63% | 84% | 50% | 96% | 95% | 76% | 99% | 59% |
| `locked_ratio` (% nonzero) | 0% | 0% | 0.5% | 0% | 0% | 0% | 0% | 0% | 0.5% | 0% | 0.5% | 0.5% | 0.5% | 1% | 0% |

Key observations:
- `idle_bucket_mean_sec` separates participants into three clear groups: **active** (P001=54s, P007=61s, P010=20s), **moderate** (P002=96s, P008=129s, P011=192s, P015=188s), **long-idle** (P003=3 404s, P014=3 731s, P012=1 212s). This is the strongest single session feature.
- P010 has 0% `idle_ge_300_ratio` — never idle ≥ 5 min. P012 and P014 have it 74–77% of the time.

---

## Domain 3 — Network Features (NetworkContextCollector)

**Signals:** `VpnStateChanged`, `WifiLinkChanged`, `WifiSsidChanged`, `LocalNetworkChanged`, `PublicIpBucketChanged`

Same state-machine approach as Session. All network identifiers (SSID, BSSID, local prefix, public IP) are hashed before storage — the aggregator only sees hash buckets, never raw IPs. The state machine tracks: `VpnOn` (bool), `WifiUp` (bool), `CurrentSsidHash`, `CurrentBssidHash`, `CurrentLocalNetworkHash`, `PublicIpBucket`.

### Feature Descriptions

| Feature | What it measures | Unit / Range |
|---------|-----------------|--------------|
| `vpn_on_ratio` | Fraction of window_ms where VPN was connected. | 0.0–1.0 |
| `primary_wifi_connected_ratio` | Fraction of window_ms where Wi-Fi link was up (does not require internet, just link). | 0.0–1.0 |
| `public_ip_known_ratio` | Fraction of window_ms where a valid public IP bucket was known (non-null, non-`"none"`, fetch status `ok`). | 0.0–1.0 |
| `vpn_flip_count` | Count of `VpnStateChanged` events in window (each = one VPN connect/disconnect). | Count 0–n |
| `wifi_flip_count` | Count of `WifiLinkChanged` events in window (Wi-Fi link up/down). | Count 0–n |
| `ssid_change_count` | Count of times the Wi-Fi SSID hash changed (different access network). | Count 0–n |
| `local_network_change_count` | Count of `LocalNetworkChanged` events (different local subnet fingerprint). | Count 0–n |
| `public_ip_bucket_change_count` | Count of `PublicIpBucketChanged` events (different public IP /24 bucket). | Count 0–n |
| `unique_wifi_ssid_count` | Distinct SSID hashes seen in this window. >1 means the user roamed between networks. | Count 0–n |
| `unique_wifi_bssid_count` | Distinct BSSID (access point) hashes seen. Higher = roaming between APs. | Count 0–n |
| `unique_local_network_count` | Distinct local network fingerprint hashes seen. | Count 0–n |
| `unique_public_bucket_count` | Distinct public IP /24 buckets seen. | Count 0–n |
| `public_ip_fetch_fail_count` | Count of public IP fetch failures (no internet or rate-limited). | Count 0–n |
| `public_ip_backoff_ratio` | Fraction of window_ms where the public IP fetch was in backoff state (throttling). | 0.0–1.0 |
| `has_net_data` | 1.0 if any network event fell in this window. | 0 or 1 |

### Observed Distributions — all 15 participants

| Feature | P001 | P002 | P003 | N004 | P005 | P006 | P007 | P008 | P009 | P010 | P011 | P012 | P013 | P014 | P015 |
|---------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|
| `vpn_on_ratio` mean | 0 | 0.71 | 0.87 | 0 | 0.81 | 0 | 0.74 | 0 | 0.74 | 0 | 0 | 0 | 0.76 | 0 | 0.77 |
| `vpn_on_ratio` % nonzero | 0% | 2% | 0.5% | 0% | 2% | 0% | 1% | 0% | 1% | 0% | 0% | 0% | 0.5% | 0% | 3% |
| `primary_wifi_connected_ratio` mean | 0.77 | 0.70 | 0.87 | 0 | 0.81 | 0 | 0.76 | 0 | 0 | 0.85 | 0 | 0 | 0 | 0 | 0.77 |
| `primary_wifi_connected_ratio` % nonzero | 1% | 1% | 0.5% | 0% | 2% | 0% | 1% | 0% | 0% | 4% | 0% | 0% | 0% | 0% | 3% |

Key observations:
- **VPN users:** P002, P003, P005, P007, P009, P013, P015 (7 of 15). N004, P001, P006, P008, P010, P011, P012, P014 never use VPN. Strong binary discriminator.
- **Ethernet-only users:** P004, P006, P008, P009, P011, P012, P013, P014 — `primary_wifi_connected_ratio` = 0 always.
- VPN and Wi-Fi signal are highly correlated — VPN users tend to be Wi-Fi users (mobile/laptop users).

---

## Domain 4 — System Resource Features (SystemResourceCollector)

**Signal:** `SystemResourceTick` — periodic sample every ~800 ms (2 s unlocked, 30 s locked per doc; actual tick rate observed = 800 ms from source count analysis). Payload has `cpu_pct`, `mem_used_pct`, `gpu_pct`, `gpu_mem_used_pct`, `net_tx_kbps`, `net_rx_kbps` plus availability flags.

### Feature Descriptions

| Feature | What it measures | Unit / Range |
|---------|-----------------|--------------|
| `cpu_usage_mean` | Mean CPU % across all available ticks in window. "Available" means `cpu_available=true` in that tick's payload. | % (0–100) |
| `cpu_usage_max` | Peak CPU % in window. | % (0–100) |
| `cpu_usage_std` | Std dev of CPU % — high = variable workload (compilation bursts, renders), low = steady workload. | % |
| `cpu_usage_high_ratio` | Fraction of ticks where CPU > 80%. | 0.0–1.0 |
| `cpu_spike_count` | Count of consecutive-tick CPU increases ≥ 20% — sudden load bursts. | Count 0–n |
| `ram_usage_mean` | Mean RAM used % in window. Reflects the user's typical number of open processes. | % (0–100) |
| `ram_usage_max` | Peak RAM used % in window. | % (0–100) |
| `ram_usage_std` | Std dev of RAM % — how much memory fluctuates (large allocations, GC events). | % |
| `ram_high_usage_ratio` | Fraction of ticks where RAM > 85%. | 0.0–1.0 |
| `ram_pressure_events` | Count of times RAM crossed 85% from below (memory pressure onset). | Count 0–n |
| `gpu_available` | 1.0 if any tick had a GPU reading. Indicates whether the machine has a discrete GPU. | 0 or 1 |
| `gpu_usage_mean` | Mean GPU utilization %. Non-zero = graphics-intensive work (gaming, ML, design, video). | % (0–100) |
| `gpu_usage_max` | Peak GPU utilization %. | % |
| `gpu_usage_std` | Std dev of GPU utilization. | % |
| `gpu_memory_usage_mean` | Mean GPU memory used %. | % |
| `gpu_high_usage_ratio` | Fraction of ticks where GPU > 70%. | 0.0–1.0 |
| `net_bytes_sent_mean` / `net_tx_kbps_mean` | Mean upload kbps across ticks. | kbps |
| `net_bytes_recv_mean` / `net_rx_kbps_mean` | Mean download kbps across ticks. | kbps |
| `net_bytes_total_mean` / `net_total_kbps_mean` | Mean total (tx+rx) kbps. | kbps |
| `net_bytes_total_max` / `net_total_kbps_max` | Peak total kbps in window. | kbps |
| `net_activity_ratio` | Fraction of ticks where total throughput > 1 kbps (any network use). | 0.0–1.0 |
| `net_throughput_std` | Std dev of total kbps — high = bursty downloads/uploads. | kbps |
| `net_spike_count` | Count of consecutive-tick kbps increases ≥ 1 000 kbps. | Count 0–n |
| `system_load_index` | Composite: `cpu_mean×0.55 + ram_mean×0.45` (no GPU), or `cpu_mean×0.4 + ram_mean×0.35 + gpu_mean×0.25` (with GPU). Single overall machine load score. | 0–100 |
| `resource_variability_index` | Mean of (cpu_std, ram_std, net_throughput_std, [gpu_std]) — how erratically resources fluctuate across the window. | Mixed units — relative |
| `cpu_ram_correlation_proxy` | `abs(cpu_usage_mean − ram_usage_mean)` — large divergence suggests the CPU and RAM are being used for different tasks (e.g., high CPU + low RAM = CPU-bound processing; low CPU + high RAM = idle-with-many-tabs). | % |
| `active_resource_ratio` | Mean of (cpu_high_ratio, net_activity_ratio, [gpu_high_ratio]) — fraction of time the machine was under active resource use. | 0.0–1.0 |
| `has_system_data` | 1.0 if any `SystemResourceTick` fell in window. Almost always 1.0 on active sessions since ticks are 800 ms. | 0 or 1 |

### Observed Distributions — all 15 participants

| Feature | P001 | P002 | P003 | N004 | P005 | P006 | P007 | P008 | P009 | P010 | P011 | P012 | P013 | P014 | P015 |
|---------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|------|
| `cpu_usage_mean` | **1.4** | 5.5 | 9.5 | 25.2 | 8.7 | **2.9** | 11.6 | 40.6 | 34.8 | 37.5 | 5.8 | 3.5 | 10.3 | 9.3 | 25.6 |
| `cpu_usage_std` | **0.43** | 3.03 | 2.88 | 4.29 | 4.01 | 1.24 | 4.39 | **8.10** | **8.41** | 5.10 | 3.12 | 1.66 | 3.48 | 3.72 | 3.20 |
| `ram_usage_mean` | **13** | 58 | 48 | 32 | 48 | **9** | **64** | 43 | **76** | 45 | 49 | 27 | **67** | 23 | 49 |
| `gpu_usage_mean` | 11.8 | 7.2 | 18.3 | 28.5 | 18.7 | 18.1 | 25.3 | 32.9 | 3.5 | **71.5** | 0.2 | 0.1 | 0.8 | 0.4 | 32.9 |
| `net_bytes_total_mean` (kbps) | 3 612 | 1 725 | 2 724 | 1 903 | **23 511** | 1 491 | **9 106** | 5 725 | 2 939 | 1 985 | **22** | **8** | 2 530 | 3 638 | 5 324 |
| `system_load_index` | **8.1** | 24.4 | 25.1 | 28.3 | 25.1 | **8.9** | 33.3 | 39.5 | 41.4 | **48.6** | 19.5 | 10.7 | 27.8 | 12.0 | 35.5 |
| `resource_variability_index` | 1 462 | 781 | 473 | 710 | 1 276 | 879 | **2 208** | 379 | 1 238 | **105** | **19** | **3.5** | 176 | 208 | 401 |

Key observations:
- `cpu_usage_mean` spans **1.4% (P001) to 40.6% (P008)** — 29× range across participants. Strongest machine fingerprint.
- `ram_usage_mean` spans **9% (P006) to 76% (P009)** — reflects hardware spec AND number of open apps.
- `gpu_usage_mean`: P010 at 71.5% is a GPU-heavy user (gaming/ML). P011/P012/P013/P014 are near-zero (integrated graphics or no GPU workload).
- `net_bytes_total_mean`: P005 (23 511 kbps) and P007 (9 106 kbps) are heavy network users. P011 (22 kbps) and P012 (8 kbps) use almost no network.
- `resource_variability_index`: P007 (2 208) is highly bursty; P010 (105), P011 (19), P012 (3.5) have very steady workloads.

---

## Domain 5 — Cross Features (gated)

These features combine session and app domains. They are only meaningful when `active_work_ratio > 0`.

**Active-work definition:**
```
active_work := !Locked
           AND DisplayState == On
           AND (PresenceKnown → !PresenceAway)
           AND (IdleKnown    → IdleBucketSec < 300)
```

| Feature | What it measures | Behavioral? |
|---------|-----------------|-------------|
| `active_work_ratio` | Fraction of window_ms where the user was genuinely at the machine (not locked, display on, not away, not idle ≥ 5 min). Key filter for all other behavioral features — a window with `active_work_ratio = 0` has no behavioral data. | Moderate — varies by work schedule |
| `app_switches_per_active_min` | App switch count divided by active minutes. Normalises switching rate to actual working time. Removes the confound of partially-idle windows. | **Strong** — pace/multitasking signature |
| `category_entropy_active` | Shannon entropy over per-category dwell shares within active intervals. High = diverse app use (context-switcher), low = focused on one category (specialist). | **Strong** — work style signature |

---

## Behavioral Feature Classification

### Tier 1 — Strong Identity Signal
*Consistent, person-specific, low overlap between participants. Primary features for a continuous auth classifier.*

**App fingerprint (all 15 participants):**
- `cat_browser_ratio` — P007 (95% of windows), P002 (84%), P008 (65%) are browser-heavy. P011 (0.2%), P012 (0%) never use browser. Clearest tool-use discriminator.
- `cat_ide_ratio` — P011 (28% nonzero, 0.90 mean), P013 (36%, 0.70), P012 (2%, 0.81) are developers. P001, N004, P006, P007, P008, P010, P014 are zero. Strongest single-category discriminator.
- `cat_terminal_ratio` — P011 (0.91 mean), P006 (0.78), P003 (0.36), P009 (0.35) use terminal heavily. Most participants near-zero.
- `cat_comms_ratio` — N004 (0.88 mean, 21% nonzero) and P013 (0.54, 18%) are comms-heavy. Others near zero.
- `cat_office_ratio` — P008 (0.54, 10% nonzero) and P013 (0.58, 6%) use office tools. Everyone else zero.
- `cat_media_ratio` — P005 only (0.99 mean, 47% of windows). Unique across all 15.
- `cat_gaming_ratio` — P008 (0.19 mean, 0.2% nonzero) and P015 (0.04, 0.4%); all others zero.
- `app_top1_share` — ranges 0.91 (P009, P013) to 0.99 (P006, P007, P012, P014). High = focused user.
- `app_unique_count` — P009 (1.7) and P013 (1.6) use more apps per window; P006/P012/P014 (1.1) are highly focused.
- `app_dwell_mean_ms` — P014 (29 058 ms), P006 (28 961), P012 (28 968) vs P009 (22 466), P013 (22 530). Captures switching rhythm.
- `category_entropy_active` — P010 (1.07) highest diversity; P002/P005/P012 (0.40–0.44) most focused.
- `app_switches_per_active_min` — P006 (21.9), P001 (15.9), N004 (16.9) switch fastest. P011/P012 (4–6) are slowest. (Note: P009 single-window outlier at 2 325 should be capped/clipped.)

**Idle / presence rhythm:**
- `idle_bucket_mean_sec` — strongest session feature. Three clear groups: **fast** (P010=20s, P001=54s, P007=61s), **moderate** (P002=96s, P008=129s, P015=188s, P011=192s), **slow/absent** (P003=3 404s, P014=3 731s, P012=1 212s).
- `idle_bucket_max_sec` — same pattern, captures worst-case absence.
- `idle_ge_300_ratio` — P010 is the only participant with 0%. P012 (74%), P014 (77%), P003 (59%) spend most of their windows in long idle.

**System workload:**
- `cpu_usage_mean` — 1.4% (P001) to 40.6% (P008), 29× range. Reflects machine spec + workload type.
- `ram_usage_mean` — 9% (P006) to 76% (P009). Number of active processes and memory-intensive tools.
- `system_load_index` — 8.1 (P001) to 48.6 (P010). Best single-number load fingerprint.
- `net_bytes_total_mean` — P005 (23 511 kbps), P007 (9 106 kbps) vs P011 (22 kbps), P012 (8 kbps). 3 000× range.
- `gpu_usage_mean` — P010 (71.5%) is a GPU-heavy user. P011/P012/P013/P014 near-zero.
- `resource_variability_index` — P007 (2 208) very bursty. P011 (19), P012 (3.5) extremely steady.

**Network identity:**
- `vpn_on_ratio` — binary split: P002/P003/P005/P007/P009/P013/P015 are VPN users; P001/N004/P006/P008/P010/P011/P012/P014 are not. Strongest network discriminator.
- `primary_wifi_connected_ratio` — N004/P006/P008/P009/P011/P012/P013/P014 are Ethernet-only (always 0). P005/P003/P002 are Wi-Fi users. Correlated with VPN usage.

### Tier 2 — Moderate Behavioral Signal
*Person-relevant but also shaped by context, schedule, or hardware. Useful as secondary features.*

- `app_switch_count` — behavioral but noisier than `app_switches_per_active_min` (not active-time normalized).
- `cpu_usage_std`, `resource_variability_index` — workload variability (compilation/render bursts vs. steady work).
- `active_work_ratio` — reflects work schedule; more about presence than identity.
- `locked_ratio` / `lock_count` / `unlock_count` — security habits vary by person but also by environment.
- `screensaver_on_ratio` / `screensaver_toggle_count` — habits around idle; correlated with `idle_bucket_*`.
- `display_toggle_count` — how often the display turns off/on in a session.
- `gpu_usage_mean` / `gpu_high_usage_ratio` — only meaningful if machine has GPU; strongly behavioral when non-zero (gaming, ML, video editing).
- `net_activity_ratio` — fraction of ticks with any network use; varies by work type.
- `primary_wifi_connected_ratio` — Ethernet users = 0 always, Wi-Fi users = 1 always, mobile users vary.
- `presence_present_ratio`, `presence_away_ratio` — presence patterns at aggregate level.
- `cpu_ram_correlation_proxy` — divergence pattern. CPU-heavy-only vs. RAM-heavy-only tasks.

### Tier 3 — Environmental / Context-Dependent
*Driven by location, network, machine configuration, or data quality. Weak individual signal; useful as context features to condition on, not classify from.*

- `vpn_flip_count`, `wifi_flip_count`, `ssid_change_count`, `local_network_change_count` — network topology changes. Usually 0. Only informative during roaming or VPN reconnects.
- `unique_wifi_ssid_count`, `unique_wifi_bssid_count`, `unique_local_network_count`, `unique_public_bucket_count` — location mobility signal.
- `public_ip_known_ratio`, `public_ip_bucket_change_count` — internet availability and location.
- `public_ip_backoff_ratio`, `public_ip_fetch_fail_count` — network quality / data availability.
- `display_on_ratio`, `display_off_ratio`, `display_dim_ratio` — mostly schedule/auto-dim behavior.
- `idle_ge_60_ratio` — superseded by `idle_bucket_mean_sec` and `idle_ge_300_ratio`.
- `cpu_usage_max`, `ram_usage_max`, `cpu_spike_count`, `ram_pressure_events` — peak/event counts are noisier than means for person identity.
- `net_throughput_std`, `net_spike_count` — bursty download events; very noisy per window.
- `net_bytes_sent_mean` / `net_bytes_recv_mean` (individually) — directional breakdown of `net_bytes_total_mean`; less useful than the total.

### Tier 4 — Data Quality Flags
*Not behavioral. Use to gate or weight windows, not as classifier inputs.*

- `has_app_data` — whether this window has any app signal. Exclude `has_app_data=0` rows entirely.
- `has_idle_data`, `has_display_data`, `has_net_data`, `has_system_data` — domain coverage flags.
- `presence_available_ratio` — fraction of window where presence was known.
- `app_confidence_high_ratio` — quality of app identification (full path vs. name fallback).
- `collector_mode_hook_ratio` — agent was in hook mode (high) vs. polling fallback (low).
- `idle_api_fail_count` — OS idle API was unreachable.
- `cpu_data_available_ratio`, `mem_data_available_ratio`, `gpu_data_available_ratio` — hardware API availability per tick.
- `no_foreground_end_count` — dwell ended without a new foreground window; edge case count.

---

### Important caveats

**`cpu_usage_mean` / `ram_usage_mean` are machine-specific.** P001's CPU at 1.5% may indicate both a different machine AND different workload. Normalise or use ratios within a session if the goal is pure behavior (not machine fingerprinting). However, for continuous auth within the same machine, this is fine.