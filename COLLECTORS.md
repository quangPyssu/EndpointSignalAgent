# Collectors - Current Technical Documentation

## Overview

Collectors are background services that monitor endpoint activity and emit `SignalEvent` payloads.

**Important current behavior**: collectors do **not** write files directly. They emit through `ISignalBroadcaster`, and downstream services handle persistence and feature extraction.

---

## Pipeline (current)

```
ApplicationUsageCollector
SessionStateCollector
NetworkContextCollector
        │
        ▼
SignalCollectorBase.WriteSignalAsync()
        │
        ▼
ISignalBroadcaster
  ├──► SignalWriter channel (bounded, wait)
  │      └──► SignalWriterService ─► SpoolFileCollector ─► spool/signals.jsonl
  └──► FeatureExtractor channel (bounded, wait)
         └──► FeatureExtractorService (live feature windows)
```

---

## Base Class

**Location**: `src/SignalCollection/Collectors/SignalCollectorBase.cs`

`SignalCollectorBase` inherits `BackgroundService` and centralizes collector emission:

- Keeps per-collector spool path metadata.
- Forwards events to `ISignalBroadcaster`.
- No internal file lock/semaphore in current implementation.

---

## Collector 1: `ApplicationUsageCollector`

**Location**: `src/SignalCollection/Collectors/ApplicationUsageCollector.cs`

### Signals emitted

- `ForegroundAppChanged`
- `AppDwell`
- `AppSwitchRate`

### Behavior summary

- Polls foreground app every `750ms`.
- Resolves foreground process with `GetForegroundWindow` + `GetWindowThreadProcessId`.
- Hashes app identity (`SHA-256`, first 12 bytes hex).
- Uses `ApplicationCategorizer` (`src/Shared/Utilities/ApplicationCategorizer.cs`) for category mapping.
- Emits periodic switch-rate windows (`60s`).
- Emits final `AppDwell` on shutdown with `reason=shutdown_flush`.

---

## Collector 2: `SessionStateCollector`

**Location**: `src/SignalCollection/Collectors/SessionStateCollector.cs`

### Signals emitted

- `SessionLock`, `SessionUnlock`
- `IdleSample`
- `ScreenSaverOn`, `ScreenSaverOff`
- `DisplayOn`, `DisplayOff`, `DisplayDimmed`

### Behavior summary

Runs three mechanisms in one service lifetime:

1. `SystemEvents.SessionSwitch` for lock/unlock events.
2. Hidden window message pump for display power broadcast events.
3. Poll loop every `2s` for idle bucket + screensaver transitions.

### Initial baseline behavior (current)

On startup, it emits:

- Initial display state (`source=initial_heuristic`, `initial=true`)
- Initial idle sample (`initial=true`)
- Initial screensaver state when available (`initial=true`)

On shutdown, it unsubscribes session events and disposes display listener.

---

## Collector 3: `NetworkContextCollector`

**Location**: `src/SignalCollection/Collectors/NetworkContextCollector.cs`

### Signals emitted

- `VpnStateChanged`
- `WifiLinkChanged`
- `WifiSsidChanged`
- `LocalNetworkChanged`
- `PublicIpBucketChanged`

### Behavior summary

- Local poll every `3s`; public IP poll every `60s`.
- VPN state from adapter heuristics (`Tunnel` + keywords).
- Wi-Fi SSID via `wlanapi.dll` + hash.
- Local network fingerprint from hashed IPv4 `/24` bucket.
- Public IP bucket from `https://api.ipify.org`, then coarse bucket + hash.
- Uses optional machine-specific salt from registry `MachineGuid`.

### Initial baseline behavior (current)

Immediately emits initial network state (`initial=true`) for VPN, Wi-Fi link, SSID, local prefix, and public bucket (if fetch succeeds).

---

## Writer Utility and Service

### `SignalWriterService`

**Location**: `src/SignalCollection/Services/SignalWriterService.cs`

- Single reader of signal-writer channel.
- For each signal, appends to spool via `SpoolFileCollector`.

### `SpoolFileCollector`

**Location**: `src/SignalCollection/Storage/SpoolFileCollector.cs`

- JSONL append writer (`FileMode.Append`, async IO).
- Line format: `{ ts, type, payload }`.
- Ensures spool directory exists.

---

## Provider Interaction

`SpoolFileSignalProvider` (`src/SignalCollection/Providers/SpoolFileSignalProvider.cs`) consumes `spool/signals.jsonl` using byte offsets (`spool/signals.offset`) and feeds `BatchProducerService`.

This means collector events can be:

1. persisted for backend send (via spool), and
2. consumed live for feature extraction,

from the same broadcast emission.

---

## Platform Notes

Collectors are currently Windows-focused (`user32.dll`, `wlanapi.dll`, `SystemEvents`, Registry).

