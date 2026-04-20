# Dataset Export Outputs (DatasetCollection mode)

This document explains everything written by **Export dataset package**.

## Where exports are created

The export folder is created under:

- `DatasetCollection:ExportRoot` (default: `exports`)

Folder name format:

- `participant_<participantId>_<yyyyMMdd_HHmmss>`

Example:

- `exports/participant_P001_20260420_093355/`

## What gets exported

When you run export, the agent creates one participant package and writes these files.

## 1) `raw_signals.jsonl` (conditional)

- Source: `spool/raw_signals.jsonl`
- Included only if the source file exists.
- Purpose: canonical raw event stream for dataset replay/re-extraction.

Each line is one JSON object (`RawCollectorSignalRecord`) with these top-level fields:

- `schema_version`
- `ts_utc`
- `device_id`
- `recording_id`
- `session_id`
- `collector`
- `signal_type`
- `signal_kind`
- `native_cadence_sec`
- `native_aggregation_sec`
- `collector_schema_version`
- `payload`

### `signal_type`: every value that can appear

Current collector-emitted values (expected in normal runtime):

#### Application usage
- `ForegroundAppChanged`
- `AppDwell`
- `AppSwitchRate`

#### Session state
- `SessionLock`
- `SessionUnlock`
- `IdleSample`
- `ScreenSaverOn`
- `ScreenSaverOff`
- `DisplayOn`
- `DisplayOff`
- `DisplayDimmed`

#### Network context
- `VpnStateChanged`
- `WifiLinkChanged`
- `WifiSsidChanged`
- `LocalNetworkChanged`
- `PublicIpBucketChanged`

#### System resource
- `SystemResourceTick`

Additional enum values that are valid but not currently emitted by active collectors:

- `Unknown`
- `Heartbeat`
- `WifiSsidHash` (legacy)

If one of these additional values appears, raw writing still succeeds; provenance falls back to `collector=UnknownCollector` and default metadata.

### Detailed signal reference (what each output means)

This section summarizes each `signal_type` written to `raw_signals.jsonl`, including source collector, expected signal kind/cadence metadata, and payload meaning.

#### `ForegroundAppChanged`
- Collector: `ApplicationUsageCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: foreground app identity changed to a new committed app.
- Payload keys:
  - `appKey`: hashed app identity
  - `category`: app category (`Browser`, `IDE`, `Terminal`, etc.)
  - `collectorMode`: `hook` or `poll`
  - `confidence`: `high` (full path) or `low` (name fallback)

#### `AppDwell`
- Collector: `ApplicationUsageCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: one foreground-app dwell segment ended.
- Payload keys:
  - `appKey`
  - `category`
  - `durationMs`: dwell duration in milliseconds
  - `reason` / `dwellReason`: `switch`, `no_foreground`, `shutdown_flush`
  - `collectorMode`
  - `confidence`

#### `AppSwitchRate`
- Collector: `ApplicationUsageCollector`
- `signal_kind`: `pre_aggregated`
- `native_cadence_sec`: `1`
- `native_aggregation_sec`: `60`
- Purpose: app-switch count over native 60s window.
- Payload keys:
  - `windowSec` (typically `60`)
  - `switches`
  - `collectorMode`

#### `SessionLock`
- Collector: `SessionStateCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: session entered locked state.
- Payload keys:
  - `source` (`WTS` or `SystemEvents`)
  - `reason`

#### `SessionUnlock`
- Collector: `SessionStateCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: session returned to unlocked state.
- Payload keys:
  - `source`
  - `reason`

#### `IdleSample`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_sample`
- `native_cadence_sec`: `null` (adaptive poll: fast/slow modes)
- `native_aggregation_sec`: `null`
- Purpose: sampled user idle/presence/screen-state context.
- Payload keys (union):
  - `idleMs`
  - `idleBucketSec`
  - `idleStatus` (`ok`, `api_fail`, `presence_update`)
  - `idlePollMode` (`fast`/`slow`)
  - `expectedCadenceSec`
  - `userPresence` (optional)
  - `presenceSource` (optional)
  - `screensaverStatus` (optional)

#### `ScreenSaverOn`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: screensaver transitioned to running.
- Payload keys:
  - `running` (`true`)
  - `screensaverStatus`

#### `ScreenSaverOff`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: screensaver transitioned to not running.
- Payload keys:
  - `running` (`false`)
  - `screensaverStatus`

#### `DisplayOn`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: display state changed to on.
- Payload keys:
  - `displayState` (`On`)
  - `source`
  - `confidence`
  - `reason` (optional)
  - `userPresence` (optional)
  - `presenceSource` (optional)

#### `DisplayOff`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: display state changed to off.
- Payload keys:
  - `displayState` (`Off`)
  - `source`
  - `confidence`
  - `userPresence` (optional)
  - `presenceSource` (optional)

#### `DisplayDimmed`
- Collector: `SessionStateCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: display state changed to dimmed.
- Payload keys:
  - `displayState` (`Dimmed`)
  - `source`
  - `confidence`
  - `userPresence` (optional)
  - `presenceSource` (optional)

#### `VpnStateChanged`
- Collector: `NetworkContextCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: VPN context changed.
- Payload keys:
  - `vpnOn`
  - `vpnAdapter`
  - `vpnConfidence`
  - `vpnReason`
  - `initial` (optional, initial snapshot)

#### `WifiLinkChanged`
- Collector: `NetworkContextCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: Wi-Fi link up/down changed.
- Payload keys:
  - `wifiUp`
  - `wifiIdentityConfidence`
  - `wifiIdentityReason`
  - `initial` (optional)

#### `WifiSsidChanged`
- Collector: `NetworkContextCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: connected Wi-Fi SSID identity changed.
- Payload keys (typical):
  - `ssidHash` (hashed SSID)
  - `wifiIdentityConfidence`
  - `wifiIdentityReason`
  - `initial` (optional)

#### `LocalNetworkChanged`
- Collector: `NetworkContextCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `3`
- `native_aggregation_sec`: `null`
- Purpose: local network identity/fingerprint changed.
- Payload keys (typical):
  - `localNetworkHash`
  - `initial` (optional)

#### `PublicIpBucketChanged`
- Collector: `NetworkContextCollector`
- `signal_kind`: `state_change`
- `native_cadence_sec`: `60`
- `native_aggregation_sec`: `null`
- Purpose: external/public IP bucket changed.
- Payload keys (typical):
  - `publicIpBucket`
  - `initial` (optional)

#### `SystemResourceTick`
- Collector: `SystemResourceCollector`
- `signal_kind`: `state_sample`
- `native_cadence_sec`: `2`
- `native_aggregation_sec`: `null`
- Purpose: periodic CPU/RAM/GPU/network sample.
- Payload keys:
  - `cpu_available`, `cpu_pct`
  - `mem_available`, `mem_used_pct`, `mem_avail_mb`, `mem_total_mb`
  - `gpu_available`, `gpu_pct`, `gpu_mem_used_pct`, `gpu_engine_active_count`
  - `swap_available`
  - `net_rx_kbps`, `net_tx_kbps`

#### `Heartbeat` (enum-valid, typically not emitted)
- Collector mapping: fallback `UnknownCollector`
- `signal_kind`: fallback `event`
- Purpose: reserved/legacy enum entry.

#### `WifiSsidHash` (enum-valid, legacy)
- Collector mapping: fallback `UnknownCollector`
- `signal_kind`: fallback `event`
- Purpose: legacy Wi-Fi SSID signal enum value.

#### `Unknown` (enum-valid)
- Collector mapping: fallback `UnknownCollector`
- `signal_kind`: fallback `event`
- Purpose: unknown/unmapped type guard value.

## 2) Manifest JSON files copied from `spool/manifests` (conditional)

- Source root: `DatasetCollection:ManifestRoot` (default: `spool/manifests`)
- Copy rule: copies `*.json` from **top directory only** (no subfolders).

Typical files copied:

- `study_manifest.json`
- `participant_manifest.json`
- `session_<sessionId>.json`
- `session_<sessionId>.annotations.json`
- `progress_state.json`

### `session_<sessionId>.json` schema (`CollectionSessionRecord`)

- `sessionId`
- `participantId`
- `studyId`
- `protocolVersion`
- `deviceId`
- `agentVersion`
- `mode`
- `startedAtUtc`
- `endedAtUtc`
- `sessionLabel`
- `normalOnly`
- `notes`
- `state` (`Idle`, `Prepared`, `Running`, `Paused`, `Completed`, `Exported` expected lifecycle)
- `updatedAtUtc`

### `session_<sessionId>.annotations.json` schema (`AbnormalAnnotationRecord[]`)

Each annotation item contains:

- `annotationId`
- `sessionId`
- `segmentType` (currently `abnormal`)
- `scenarioCode`
- `scenarioLabel`
- `startedAtUtc`
- `endedAtUtc`
- `initiatedBy`
- `confidence`
- `notes`
- `isComplete`
- `updatedAtUtc`

### `progress_state.json` schema (`ProgressStateRecord`)

- `studySpanWeeks`
- `validCollectionDays`
- `totalSessionsCompleted`
- `totalRuntimeHours`
- `totalActiveHours`
- `abnormalScenariosCompleted`
- `abnormalMinutes`
- `coreSignalCoverageDaysOk`
- `completionStatus`
- `completionRatio`
- `lastUpdatedUtc`

## 3) `progress_snapshot.json`

- Generated at export time from current progress service state.
- Same schema as `ProgressStateRecord` above.
- Purpose: frozen export-time progress snapshot.

## 4) `collector_health_snapshot.json`

Schema (`CollectorHealthSnapshot`):

- `signalWriterRunning`
- `sessionStateCollectorRunning`
- `applicationUsageCollectorRunning`
- `networkContextCollectorRunning`
- `systemResourceCollectorRunning`
- `collectionPaused`
- `capturedAtUtc`

Current implementation sets collector running flags to `true` at snapshot creation and records current pause status.

## 5) `dataset_export_manifest.json`

Schema (`DatasetExportManifest`):

- `exportTimeUtc`
- `agentVersion`
- `schemaVersion` (currently `dataset-collection-v1`)
- `exportType` (currently `participant`)
- `selection` (participant id passed to export)
- `checksums` (map of `fileName -> SHA256 hex`)

### Checksum behavior

- Checksums are computed for files added before writing this manifest:
  - copied raw file (if present)
  - copied manifest JSON files
  - `progress_snapshot.json`
  - `collector_health_snapshot.json`
- `dataset_export_manifest.json` itself is **not** included in `checksums`.

## Typical exported folder layout

```text
exports/
  participant_P001_20260420_093355/
    raw_signals.jsonl                       (if available)
    study_manifest.json                     (if available)
    participant_manifest.json               (if available)
    session_<id>.json                       (0..n)
    session_<id>.annotations.json           (0..n)
    progress_state.json                     (if available)
    progress_snapshot.json
    collector_health_snapshot.json
    dataset_export_manifest.json
```

## How to trigger export

From tray menu:

- **Export dataset package**

The UI shows the final absolute export folder path after success.
