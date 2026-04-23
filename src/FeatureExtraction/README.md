# FeatureExtraction

Consumes live signal broadcast, computes windowed behavioral features using event-time
windowing, persists rows to SQLite, and optionally uploads to backend.

## Pipeline placement

```
featureExtractorChannel (BroadcastSignal)
  └── FeatureExtractorService
        ├── event buffer (sorted, in-memory)
        ├── SlidingWindowing (epoch-aligned 60 s windows, 30 s step)
        ├── Five aggregators (App / Session / Network / System / Cross)
        └── FeatureStore (SQLite: spool/features.db)
              ├── FeatureUploadService → Backend /features  [Normal mode]
              └── FeatureCleanupService (7-day retention)    [Normal mode]
```

## Files

### `Services/FeatureExtractorService.cs`

The core service. Runs two concurrent loops:
1. **Signal reader loop** — `ReadAllAsync` on the feature channel; calls `ProcessSignal` + `TryEmitDueWindowsAsync`.
2. **Background timer loop** — every 2 s calls `TryEmitDueWindowsAsync` (ensures windows emit even when signal rate is low).

**Event-time watermark:** `_maxEventTsUtc` tracks the maximum seen event timestamp. A window
with `StartUtc` is complete when `_maxEventTsUtc >= StartUtc + WindowSec`.

**Context slice per window:** `[Wstart - (WindowSec + StepSec), Wend)` — 90 s of lookback
for the current 60 s window. Allows stateful aggregators to reconstruct state at window start.

**Buffer compaction:** After advancing the window pointer, events older than
`nextWindowStart - WindowSec - StepSec - 10s` are dropped. Exception: the latest event
before the cutoff is retained for each stateful signal type (session lock, display, VPN, Wi-Fi,
public IP, screensaver, idle).

**Concurrency:** `_bufferLock` protects `_eventBuffer` and `_maxEventTsUtc`. `_emitLock`
(semaphore 1,1) prevents concurrent emission rounds.

**On-demand extraction** (`ExtractFeaturesFromFileAsync`): reads `spool/raw_signals.jsonl`,
parses both `raw-collector-v1` and legacy `SignalEvent` lines, iterates all window profiles,
applies `ShouldIncludeSignal` to skip pre-aggregated signals wider than the target window.

**Forced windowing constants:** Live extraction always uses `FeatureSchema.WindowSec=60`
and `FeatureSchema.StepSec=30`, regardless of `FeatureExtractorOptions` values. A warning is
logged if config values differ.

**Early exit conditions:**
- `FeatureExtractorOptions.Enabled = false` → service exits immediately.
- `FeatureExtractorOptions.EnableLiveExtraction = false` → no live loop (on-demand still works).
- DatasetCollection mode forces `EnableLiveExtraction = false` at registration time.

### `Services/FeatureUploadService.cs`

**Normal mode only.** Runs every 2 minutes. Calls `IFeatureStore.GetUnsentAsync(limit: 50)`,
POSTs to `Backend/FeaturesPath`, calls `MarkAsSentAsync` on success.
Exponential backoff (5 s → 60 s max) on failure.

### `Services/FeatureCleanupService.cs`

**Normal mode only.** Runs daily. Calls `IFeatureStore.DeleteOlderThanAsync(now - 7 days)`.
Only deletes rows where `sent_flag = 1`.

### `Services/KeyboardCommandService.cs`

Global hotkey listener for operator commands:
- `Ctrl+E` → `ExtractFeaturesFromFileAsync("spool/raw_signals.jsonl")` (on-demand extraction)
- `Ctrl+P` → export unsent feature rows to CSV
- `Ctrl+O` → export all feature rows to CSV (also triggered from tray "Export all features to CSV")
- `Ctrl+Shift+X` → `IFeatureStore.ClearAllAsync()` (destructive — clears entire DB)

`ExportAllFeatureDataAsync(ct)` is also callable directly from `TrayApplicationContext`.

### `SignalAggregator/FeatureSchema.cs`

Single source of truth for feature schema:
- `FeatureVersion = "1.2"` — included in every stored row.
- `WindowSec = 60`, `StepSec = 30` — fixed live-extraction constants.
- `AppColumns` (24 cols), `SessionColumns` (19 cols), `NetworkColumns` (18 cols),
  `CrossColumns` (3 cols), `SystemColumns` (35 cols).
- `AllColumns` — concatenation in order: App → Session → Network → Cross → System.
- `CategoryToColumn` — lowercase category string → column name for app category ratios.

### `SignalAggregator/Windowing.cs`

`SlidingWindowing` — static utility:
- `AlignToStepUtc(ts, stepSec)` — floors timestamp to nearest step boundary.
- `EnumerateWindowStarts(from, to, windowSec, stepSec)` — yields `SlidingWindow` structs.
- `OverlapMs(a, b)` — `max(0, min(a.end, b.end) - max(a.start, b.start))`.
- `SplitSegmentAcrossWindows` — distributes a time segment across overlapping windows.

`SlidingWindow` record: `StartUtc`, `EndUtc` (= StartUtc + WindowSec).

### `SignalAggregator/AppFeatureAggregator.cs`

Consumes `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate`.

Dwell reconstruction: each `AppDwell` event defines a segment `[ts - durationMs, ts)`.
Clipped to window via overlap math. Negative/zero durations are skipped.

App switch count: preferred from `AppSwitchRate.switches` if present in window; falls back to
count of `ForegroundAppChanged` events.

### `SignalAggregator/SessionFeatureAggregator.cs`

Builds piecewise-constant time slices from session-state events (lock, display, screensaver,
presence). State at `window.StartUtc` is reconstructed by replaying pre-window events from
the context slice. Integrates slice duration over window to compute ratios.

### `SignalAggregator/NetworkFeatureAggregator.cs`

Same piecewise approach as session aggregator for VPN, Wi-Fi link, SSID, local network,
public IP. Tracks unique set counts and flip counts.

### `SignalAggregator/SystemResourceFeatureAggregator.cs`

Consumes raw `SystemResourceTick` events. Computes mean/max/std/ratio/spike-count for
CPU, RAM, GPU, network. Derives composite indices:
- `system_load_index` — weighted CPU + RAM + GPU.
- `resource_variability_index` — CV of the load index series.
- `cpu_ram_correlation_proxy` — sign-aligned Pearson proxy.
- `active_resource_ratio` — fraction of ticks where CPU or RAM > threshold.

### `SignalAggregator/CrossFeatureAggregator.cs`

Produces cross-domain features from session and app aggregator results:
- `active_work_ratio` — fraction of window where display on AND not locked AND not screensaver.
- `app_switches_per_active_min` — app switches normalized to active minutes.
- `category_entropy_active` — Shannon entropy of app category distribution during active time.

### `SignalAggregator/FeatureResultModels.cs`

`FeatureSignal` — in-memory signal representation used by aggregators:
`(DateTimeOffset TimestampUtc, SignalEventType Type, IReadOnlyDictionary<string,string> Payload, int? NativeAggregationSec)`.

`AggregatorResult` — holds aggregator output: `IReadOnlyDictionary<string, double> Features`.

### `SignalAggregator/WindowProfile.cs`

Profiles for on-demand extraction:
- `W60S30` — 60 s window, 30 s step (canonical live profile)
- `W120S60` — 120 s window, 60 s step
- `W30S15` — 30 s window, 15 s step
- `DefaultProfiles` — all three, used when no specific profile is requested.

### `Storage/FeatureStore.cs`

SQLite store at `spool/features.db`. Lazy schema init on first use; `EnsureColumnAsync` adds
columns idempotently to handle in-place schema upgrades.

Table `feature_rows` columns:
```
id, device_id, window_sec, window_start_ts, feature_version,
window_profile_id, window_size_sec, slide_sec,
event_time_start, event_time_end, extraction_run_id,
feature_schema_version, collector_schema_version, source_counts_json,
features_json, sent_flag, sent_at, created_at
```

Indexes: `idx_sent_flag`, `idx_window_start`, `idx_device_id`.

Key operations:
- `StoreAsync(row)` → inserts, returns auto-increment `id`.
- `GetUnsentAsync(limit)` → rows where `sent_flag=0`, ordered by `window_start_ts`.
- `MarkAsSentAsync(ids)` → sets `sent_flag=1`, `sent_at=now`.
- `DeleteOlderThanAsync(cutoff)` → deletes sent rows older than cutoff.
- `ClearAllAsync()` → deletes all rows (destructive, triggered by Ctrl+Shift+X).
- `GetAllAsync(limit)`, `GetRangeAsync(start,end)`, `GetLatestAsync(count)` — query helpers.

### `Contracts/FeatureRow.cs`

Immutable record (17 fields). Factory method `FeatureRow.CreateNew(...)` for creating unsent rows.

### `Contracts/FeatureBatchContracts.cs`

DTOs for the backend `/features` POST:
- `FeatureBatchRequest` — `DeviceId` + list of `FeatureRowDto`.
- `FeatureRowDto` — subset of `FeatureRow` fields for wire format.

### `Broadcasting/IFeatureExtractorChannelReader.cs`

DI wrapper around `ChannelReader<BroadcastSignal>` for the feature channel.

### `Configuration/FeatureExtractorOptions.cs`

Options section: `FeatureExtractor`

| Property | Default | Valid range |
|---|---|---|
| `Enabled` | `true` | — |
| `EnableLiveExtraction` | `true` | forced false in DatasetCollection mode |
| `WindowSizeSeconds` | 60 | 10–3600 |
| `WindowSlideSeconds` | 30 | 5–3600 |
| `MaxEventsPerWindow` | 1000 | 100–100000 |

Note: `WindowSizeSeconds` and `WindowSlideSeconds` are validated but not used for live extraction —
the service always uses `FeatureSchema.WindowSec=60` / `FeatureSchema.StepSec=30`.

## Invariants

- Feature schema changes require coordinated updates: `FeatureSchema.cs`, the affected aggregator,
  `FeatureVersion` constant, and downstream backend consumers.
- `spool/raw_signals.jsonl` is the canonical on-demand replay source. `spool/signals.jsonl` is
  the legacy send-pipeline format.
- `ClearAllAsync` is non-recoverable. Never expose it in unattended/automated flows.
