# Feature Extraction Schema (v1.1)

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
- Cross features are explicitly gated by session-derived `active_work` intervals.

## app_window_features (ApplicationUsageCollector only)
- `app_switch_count` (prefer `AppSwitchRate.switches`; fallback to `ForegroundAppChanged` count)
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
- `collector_mode_hook_ratio`
- `has_app_data`

## session_window_features (SessionStateCollector only)
- `lock_count`
- `unlock_count`
- `display_toggle_count`
- `screensaver_toggle_count`
- `locked_ratio`
- `display_off_ratio`
- `display_dim_ratio`
- `display_on_ratio`
- `screensaver_on_ratio`
- `presence_away_ratio`
- `presence_present_ratio`
- `idle_bucket_mean_sec`
- `idle_bucket_max_sec`
- `idle_ge_60_ratio`
- `idle_ge_300_ratio`
- `has_idle_data`
- `has_display_data`
- `idle_api_fail_count`
- `presence_available_ratio`

## network_window_features (NetworkContextCollector only)
- `vpn_on_ratio`
- `primary_wifi_connected_ratio`
- `public_ip_known_ratio`
- `vpn_flip_count`
- `wifi_flip_count`
- `ssid_change_count`
- `local_network_change_count`
- `public_ip_bucket_change_count`
- `unique_wifi_ssid_count`
- `unique_wifi_bssid_count`
- `unique_local_network_count`
- `unique_public_bucket_count`
- `public_ip_fetch_fail_count`
- `public_ip_backoff_ratio`
- `has_net_data`

## cross_window_features (gated)
Session-defined active-work state:
- `active_work := !locked AND display=On AND (presence != away when known) AND (idleBucketSec < 300 when known)`

Features:
- `active_work_ratio`
- `app_switches_per_active_min`
- `category_entropy_active`

## Data Quality
Missingness is represented explicitly by:
- `has_app_data`, `has_idle_data`, `has_display_data`, `has_net_data`
- `presence_available_ratio`
- API/status quality counts and ratios (`idle_api_fail_count`, `public_ip_fetch_fail_count`, `public_ip_backoff_ratio`)

## v1 Compatibility
Legacy columns are still present/compatible:
- `cat_browser_ratio`, `cat_ide_ratio`, `cat_comms_ratio`, `cat_other_ratio`
- `lock_count`, `locked_ratio`, `display_off_ratio`, `screensaver_on_ratio`
- `idle_bucket_mean_sec`, `idle_bucket_max_sec`, `idle_ge_60_ratio`
- `vpn_on_ratio`, `wifi_up_ratio` (alias of `primary_wifi_connected_ratio`)
- `local_prefix_change_count` (alias of `local_network_change_count`)

