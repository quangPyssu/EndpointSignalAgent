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

**Supporting components**:

- `src/SignalCollection/Collectors/Network/NetworkContextAbstractions.cs`
- `src/SignalCollection/Collectors/Network/NetworkContextModels.cs`
- `src/SignalCollection/Collectors/Network/NetworkContextLogic.cs`
- `src/SignalCollection/Collectors/Network/NetworkContextImplementations.cs`

### Signals emitted

- `VpnStateChanged`
- `WifiLinkChanged`
- `WifiSsidChanged`
- `LocalNetworkChanged`
- `PublicIpBucketChanged`

### Behavior summary

- Uses a modular pipeline (snapshot provider, primary interface resolver, route reader, RAS reader, WLAN reader, hashing service, clock).
- Tick interval is `3s` (`PeriodicTimer`), with separate public IP refresh cadence (`60s`) and exponential backoff on failures.
- Public IP providers are currently failover-based: `ipify` and `ifconfig.me`.
- Network state changes are debounced with `SignalDebouncer<T>` before emission:
  - minimum `2` consecutive samples or `6s` stability window.
- Emits confidence and reason metadata alongside core values for VPN and Wi-Fi identity quality.

### VPN decision model

VPN state is produced by `VpnDecisionEngine` using multiple signals (not only adapter type):

- Primary interface tunnel/PPP or known VPN adapter keywords.
- Split-tunnel route pattern detection from route table.
- Active RAS connections (`rasapi32`) as a strong signal.
- Fallback heuristic on any up interface containing VPN-like naming.

Payload includes `vpnOn`, `vpnAdapter`, `vpnConfidence`, `vpnReason`.

### Wi-Fi identity model

- Wi-Fi identity is computed only when primary interface is Wi-Fi and up.
- Uses native WLAN API by interface GUID.
- Emits hashed SSID and hashed BSSID (`wifiBssidHash`) when available.
- Includes quality metadata:
  - `wifiIdentityConfidence` (`high|medium|low`)
  - `wifiIdentityReason` (`connected`, `disconnected`, `api_fail`, `not_wifi_primary`, etc.)

### Local network fingerprint model

`LocalNetworkFingerprintBuilder` emits:

- `localPrefixHash` (`localPrefix`) from a coarse IP prefix.
  - IPv4: `/24`
  - IPv6: `/64`
- `localNetworkHash` composite hash derived from prefix + gateway set + DNS set.
- `localIpFamily` and `localNetworkReason`.

### Public IP model

- Public IP bucket is coarsened then hashed:
  - IPv4 `/24`
  - IPv6 `/48`
- Payload includes:
  - `publicIpBucket`
  - `publicIpAgeSeconds`
  - `publicIpFetchStatus` (`ok`, `fail`, `backoff`)

### Hashing/salt behavior

`HashingService` uses stable salt from `StableSaltProvider`:

1. Windows `MachineGuid` when available.
2. Persisted secret file (`ProgramData` fallback to `LocalAppData`) when `MachineGuid` unavailable.
3. Last-resort machine/user fallback string.

All published identity values are hashed; plaintext SSID/BSSID/IP are not emitted.

### Initial baseline behavior (current)

On startup it forces a public IP attempt, initializes debouncers from first snapshot, and emits `initial=true` events for:

- `VpnStateChanged`
- `WifiLinkChanged`
- `WifiSsidChanged`
- `LocalNetworkChanged`
- `PublicIpBucketChanged`

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

