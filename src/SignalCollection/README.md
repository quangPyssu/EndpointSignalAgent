# SignalCollection

Collects raw endpoint signals, broadcasts them to downstream consumers, and manages
spool file persistence and backend batch sending.

## Data flow

```
Collectors (4)
     │  WriteSignalAsync → ISignalBroadcaster
     ▼
SignalBroadcaster
  ├──► signalWriterChannel (BroadcastSignal, cap 1000, Wait)
  │         └── SignalWriterService
  │               ├── SpoolFileCollector → spool/signals.jsonl
  │               └── RawSignalFileCollector → spool/raw_signals.jsonl
  └──► featureExtractorChannel (BroadcastSignal, cap 1000, Wait)
            └── FeatureExtractorService (see src/FeatureExtraction/)

[Normal mode only]
spool/signals.jsonl
  └── SpoolFileSignalProvider (offset cursor: spool/signals.offset)
        └── BatchProducerService → Channel<SignalBatchRequest> (DropOldest)
              └── BatchSendService → Backend /send
```

## Files

### `Collectors/SignalCollectorBase.cs`

Abstract `BackgroundService` base for all collectors.

- Holds per-collector `SpoolPath` used to route `SignalWriterService`.
- Calls `ISignalBroadcaster.BroadcastAsync(signal)` for every emitted event.
- Checks `ICollectionControl.IsPaused` before emitting — paused collectors drop events silently.

### `Collectors/ApplicationUsageCollector.cs`

Monitors foreground application changes. Emits: `ForegroundAppChanged`, `AppDwell`, `AppSwitchRate`.

Architecture:
- State machine (`ApplicationUsageStateMachine`) receives observations from an unbounded channel.
- Three concurrent timers: 3 s fallback poll, 200 ms debounce poll (during pending transitions), 1 s switch-rate tick.
- Primary foreground source: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`.
- Fallback: `GetForegroundWindow` + `GetWindowThreadProcessId`.

Debounce commit criteria (either):
- 2 consecutive confirmations of same PID, OR
- 400 ms elapsed since first observation.

Inactivity threshold:
- No window detected for 2 polls OR 1.5 s → emits `AppDwell` with `reason=no_foreground`.

Process resolution:
- High confidence: `OpenProcess` + `QueryFullProcessImageName` → full exe path.
- Low confidence fallback: `Process.GetProcessById().ProcessName`.

App identity is hashed via SHA-256 (first 12 bytes → 24 hex chars) with stable salt.
Category via `ApplicationCategorizer.Categorize(exeName)`.

### `Collectors/SessionStateCollector.cs`

Monitors session lock, idle, screensaver, display state, and user presence. Emits:
`SessionLock`, `SessionUnlock`, `IdleSample`, `ScreenSaverOn`, `ScreenSaverOff`,
`DisplayOn`, `DisplayOff`, `DisplayDimmed`.

Architecture:
- `SessionEventWindowListener` — message-only window (`HWND_MESSAGE`) on dedicated STA thread.
  Registers for three power GUIDs and `WTSRegisterSessionNotification`.
- Adaptive idle polling: 2 s normally, 30 s when session locked or user presence is `away`.
- All state transitions debounced via `DebouncedStateTracker<T>` (2 confirmations or 2 s settle).

Lock/unlock dual path:
- Primary: `WM_WTSSESSION_CHANGE` (`source=WTS`).
- Fallback: `SystemEvents.SessionSwitch` (`source=SystemEvents`), suppressed if WTS event seen within 3 s.

Display state power GUIDs:
- `GUID_CONSOLE_DISPLAY_STATE` (6FE69556-…): values 0/1/2 → Off/On/Dimmed.
- `GUID_MONITOR_POWER_ON` (02731015-…): 0/1 → Off/On.
- `GUID_SESSION_USER_PRESENCE` (3C0F4548-…): 0=present, 1=away.

Idle bucketing (reported in `IdleSample.idleBucketSec`):
- < 120 s → 5 s buckets; 120–600 s → 15 s buckets; > 600 s → 60 s buckets.

On startup, emits one synthetic `DisplayOn` (confidence=low, source=initial_unknown) and one
initial `IdleSample`.

### `Collectors/NetworkContextCollector.cs`

Monitors VPN, Wi-Fi, local network, and public IP changes. Emits:
`VpnStateChanged`, `WifiLinkChanged`, `WifiSsidChanged`, `LocalNetworkChanged`,
`PublicIpBucketChanged`.

Architecture:
- Modular pipeline: snapshot provider → primary interface resolver → route reader → RAS reader
  → WLAN reader → hashing service.
- Tick interval: 3 s (`PeriodicTimer`). Public IP refresh: 60 s with exponential backoff on failure.
- Debounced with `SignalDebouncer<T>` (2 samples or 6 s stability window).

VPN decision: `VpnDecisionEngine` combines tunnel/PPP adapter type, route patterns, active RAS
connections, and VPN keyword heuristics.

Hashing: `HashingService` with stable salt from `StableSaltProvider`
(Windows `MachineGuid` → persisted secret file → machine/user fallback).
All identity values (SSID, BSSID, IP, local prefix) are hashed before emission.

Public IP coarsening: IPv4 → /24, IPv6 → /48, then hashed.

Emits initial snapshot with `initial=true` on startup for all five signal types.

Supporting files (sub-namespace `Network/`):
- `NetworkContextAbstractions.cs` — interfaces for each pipeline component.
- `NetworkContextModels.cs` — tick state, debounce state records.
- `NetworkContextLogic.cs` — VPN engine, fingerprint builder, debouncer logic.
- `NetworkContextImplementations.cs` — concrete implementations using Win32 APIs.

### `Collectors/SystemResourceCollector.cs`

Raw 2 s sampler. Emits one `SystemResourceTick` per tick with instantaneous values.
No rolling averages — all aggregation happens in `SystemResourceFeatureAggregator`.

Payload: `cpu_available`, `cpu_pct`, `mem_available`, `mem_used_pct`, `mem_avail_mb`,
`mem_total_mb`, `gpu_available`, `gpu_pct`, `gpu_mem_used_pct`, `gpu_engine_active_count`,
`swap_available` (always false), `net_rx_kbps`, `net_tx_kbps`.

First CPU sample initializes baseline and emits `cpu_available=false` until a delta is available.
GPU absence is non-fatal (`gpu_available=false`, numeric fields 0).

### `Broadcasting/ISignalBroadcaster.cs` / `SignalBroadcaster.cs`

`SignalBroadcaster` holds an array of `ChannelWriter<BroadcastSignal>` and writes to all of them.

`BroadcastSignal` carries: `TimestampUtc`, `Type` (`SignalEventType` enum), `Payload`
(`IReadOnlyDictionary<string, string>`), `SpoolPath`.

### `Broadcasting/ISignalWriterChannelReader.cs`

Wrapper interface around `ChannelReader<BroadcastSignal>` for the writer channel.
Allows DI injection without exposing the raw channel.

### `Services/SignalWriterService.cs`

Reads the writer channel. For each `BroadcastSignal`:
1. Writes a `SignalEvent` (legacy format) to `spool/signals.jsonl` via `SpoolFileCollector`.
2. Writes a `RawCollectorSignalRecord` (raw-collector-v1 envelope) to `spool/raw_signals.jsonl`
   via `RawSignalFileCollector`.

Signal provenance metadata is resolved from `SignalProvenanceCatalog.Resolve(signalType)`.

`RecordingId` is a GUID generated per service instance. `DeviceId` is lazily resolved from
`EnrollmentStore` (cached; falls back to `MachineName`).

### `Services/CollectionControl.cs`

`ICollectionControl` — thread-safe pause/resume via `Interlocked`. Collectors check
`IsPaused` before broadcasting. When paused, signals are silently dropped.

### `Services/BatchProducerService.cs`

**Normal mode only.** Reads from `SpoolFileSignalProvider`, serializes a `SignalBatchRequest`,
and writes to `Channel<SignalBatchRequest>` at the interval from `AgentState.GetReportSecondsOrDefault`.

Waits for enrollment before starting. Completes the channel writer on exit.

### `Services/BatchSendService.cs`

**Normal mode only.** Reads `Channel<SignalBatchRequest>` and POSTs to `Backend/SendPath`.

### `Storage/SpoolFileCollector.cs`

JSONL appender for `spool/signals.jsonl`. Opens with `FileMode.Append` and `FileShare.ReadWrite`
per write. Line format: `{"ts":"...","type":"...","payload":{...}}`.

### `Storage/RawSignalFileCollector.cs`

JSONL appender for `spool/raw_signals.jsonl`. Uses exponential backoff retry (up to 4 attempts,
50 ms base) to survive transient IO contention. CamelCase JSON serialization.

### `Providers/SpoolFileSignalProvider.cs`

Reads `spool/signals.jsonl` starting at the byte offset stored in `spool/signals.offset`.
Returns batches of `SignalEvent`. Advances and persists the offset after each batch.

### `Providers/HeartbeatSignalProvider.cs`

Provides periodic heartbeat signals (not currently consumed by any aggregator).

### `Contracts/RawCollectorSignalRecord.cs`

The canonical raw record schema (`schema_version = "raw-collector-v1"`):
`schema_version`, `ts_utc`, `device_id`, `recording_id`, `session_id`,
`collector`, `signal_type`, `signal_kind`, `native_cadence_sec`, `native_aggregation_sec`,
`collector_schema_version`, `payload`.

## Invariants

- `spool/raw_signals.jsonl` is the canonical dataset source for replay. Never truncate it during
  an active study.
- `spool/signals.jsonl` + `spool/signals.offset` are the backend send pipeline state.
  Deleting `signals.offset` causes re-send from the beginning.
- Collectors are Windows-only (`user32.dll`, `wlanapi.dll`, `SystemEvents`, Registry).
- Pause stops emission at the broadcaster level; the channel is not drained.
