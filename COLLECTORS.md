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

### Architecture

Uses a **state machine pattern** with:
- **Input channel**: Unbounded, single-reader, multi-writer channel for observations and timer ticks
- **Three concurrent timers**:
  1. Fallback polling: `3s` interval (always active)
  2. Debounce polling: `200ms` interval (activated only during pending transitions)
  3. Switch rate tick: `1s` interval (for window emission)
- **Foreground sources**:
  - Primary: `SetWinEventHook` with `EVENT_SYSTEM_FOREGROUND` (preferred, event-driven)
  - Fallback: Manual polling via `GetForegroundWindow` + `GetWindowThreadProcessId`

### State machine behavior

The `ApplicationUsageStateMachine` manages:

1. **Current slice**: Active foreground app with start timestamp
2. **Pending candidate**: Tentative new app awaiting confirmation (debounced)
3. **Inactivity tracking**: Detects when no foreground window exists

#### Debouncing logic

Transitions to a new app require **either**:
- `2` consecutive confirmations (same PID observed twice), **OR**
- `400ms` elapsed duration since first observation

When a pending candidate exists:
- Debounce polling activates at `200ms` intervals
- Stops when candidate is committed or cancelled

#### Inactivity handling

When no foreground window detected:
- Opens inactive period on first observation
- Closes current app dwell after **either**:
  - `2` consecutive inactive polls, **OR**
  - `1.5s` elapsed since inactivity started
- Emits `AppDwell` with `reason=no_foreground`

### Process resolution

Uses `WindowsProcessInfoResolver` with two-tier resolution:

1. **Primary** (high confidence): `OpenProcess` + `QueryFullProcessImageName`
   - Requires `PROCESS_QUERY_LIMITED_INFORMATION` access
   - Returns full executable path (e.g., `C:\Tools\Code.exe`)
   - Sets `confidence=high`
2. **Fallback** (low confidence): `Process.GetProcessById` + `ProcessName`
   - Returns only process name (e.g., `code`)
   - Sets `confidence=low`

### Hashing and categorization

- App identity is hashed via `HashingService.HashStable($"app|{hashInput}")` where:
  - `hashInput` is full path (high confidence) or process name (low confidence)
- Salt is from `StableSaltProvider` (same as `NetworkContextCollector`)
- Category is determined by `ApplicationCategorizer.Categorize(exeName)` with normalization (trim, case-insensitive)
- Output hash is 24 characters (first 12 bytes of SHA-256 in hex)

### Signal emission details

#### `ForegroundAppChanged`
Emitted when a pending candidate commits to become the current app.

Payload fields:
- `appKey` (24-char hashed identity)
- `category` (`Browser`, `IDE`, `Comms`, `Terminal`, `System`, `Other`)
- `collectorMode` (`hook` if event-driven, `poll` if fallback)
- `confidence` (`high` if full path resolved, `low` if only process name)

#### `AppDwell`
Emitted when an app's foreground session ends.

Payload fields:
- `appKey` (24-char hashed identity)
- `category`
- `durationMs` (milliseconds from start to end, non-negative)
- `reason` / `dwellReason` (both set to same value):
  - `switch`: User switched to different app
  - `no_foreground`: No foreground window detected
  - `shutdown_flush`: Collector shutdown
- `collectorMode`
- `confidence`

**Important**: Dwell duration does **not** include inactive time. If inactivity threshold is crossed, dwell ends at `_inactiveSince`, not current time.

#### `AppSwitchRate`
Emitted every `60s` window.

Payload fields:
- `windowSec` (always `60`)
- `switches` (count of **committed** app switches in window)
- `collectorMode`

**Important**: Only counts switches between different apps. Rejected/debounced transient switches are not counted.

### Startup behavior

1. Attempts to install foreground hook
2. Takes initial poll immediately
3. Starts all three timer loops
4. Processes observations through state machine
5. May emit initial `ForegroundAppChanged` if foreground app detected

### Shutdown behavior

1. Cancels all timer tasks
2. Waits for timer task completion
3. Calls `ApplicationUsageStateMachine.FlushShutdownAsync`
4. Emits final `AppDwell` for current app (if any) with `reason=shutdown_flush`
5. Emits final `AppSwitchRate` if window elapsed
6. Unhooks event source and disposes resources

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
- Public IP providers are currently failover-based: `ipify` (`https://api.ipify.org`) and `ifconfig.me` (`https://ifconfig.me/ip`).
- Uses shared `HttpClient` with `2s` timeout and `5-minute` pooled connection lifetime.
- Network state changes are debounced with `SignalDebouncer<T>` before emission:
  - minimum `2` consecutive samples or `6s` stability window.
- Emits confidence and reason metadata alongside core values for VPN and Wi-Fi identity quality.
- Tracks internal health counters: `wlan_success`, `wlan_fail`, `ras_success`, `ras_fail`, `route_read_success`, `route_read_fail`, `public_ip_success`, `public_ip_fail`.

### VPN decision model

VPN state is produced by `VpnDecisionEngine` using multiple signals (not only adapter type):

- Primary interface tunnel/PPP or known VPN adapter keywords.
- Split-tunnel route pattern detection from route table.
- Active RAS connections (`rasapi32`) as a strong signal.
- Fallback heuristic on any up interface containing VPN-like naming.

Payload fields:
- `vpnOn` (boolean as string)
- `vpnAdapter` (adapter fingerprint or "none")
- `vpnConfidence` (confidence level)
- `vpnReason` (reason code)

### Wi-Fi identity model

Wi-Fi identity resolution follows this logic:

1. If no primary interface or primary is down → `wifiUp=false`, `reason=no_primary`
2. If primary is not Wi-Fi → `wifiUp=false`, `reason=not_wifi_primary`
3. If WLAN API unavailable/fails → `wifiUp=true`, `reason=api_fail`, `ssid/bssid=unknown`
4. If Wi-Fi disconnected → `wifiUp=false`, `reason=disconnected`
5. If Wi-Fi connected → `wifiUp=true`, `reason=connected`, with hashed SSID and BSSID

Payload fields for `WifiLinkChanged`:
- `wifiUp` (boolean as string)
- `wifiIdentityConfidence` (`high`, `medium`, `low`)
- `wifiIdentityReason` (one of: `no_primary`, `not_wifi_primary`, `api_fail`, `disconnected`, `connected`)

Payload fields for `WifiSsidChanged`:
- `wifiSsid` (hashed SSID or "none"/"unknown")
- `wifiUp` (boolean as string)
- `wifiBssidHash` (hashed BSSID or "none")
- `wifiIdentityConfidence` (`high`, `medium`, `low`)
- `wifiIdentityReason` (reason code as above)

### Local network fingerprint model

`LocalNetworkFingerprintBuilder` emits:

- `localPrefix` and `localPrefixHash` (both set to the same hashed value from coarse IP prefix)
  - IPv4: `/24`
  - IPv6: `/64`
- `localNetworkHash` composite hash derived from prefix + gateway set + DNS set.
- `localIpFamily` (IP family indicator)
- `localNetworkReason` (reason code)

### Public IP model

- Public IP bucket is coarsened then hashed:
  - IPv4 `/24`
  - IPv6 `/48`
- Fetches with `2s` HTTP timeout
- Failover between providers on error
- Exponential backoff on failures (min 2^failureCount seconds, capped at 300s)
- Provider index rotates on each failure

Payload fields:
- `publicIpBucket` (hashed bucket or "none")
- `publicIpAgeSeconds` (seconds since last successful fetch, or -1 if never succeeded)
- `publicIpFetchStatus` (`ok`, `fail`, `backoff`)

### Hashing/salt behavior

`HashingService` uses stable salt from `StableSaltProvider`:

1. Windows `MachineGuid` when available.
2. Persisted secret file (`ProgramData` fallback to `LocalAppData`) when `MachineGuid` unavailable.
3. Last-resort machine/user fallback string.

All published identity values are hashed; plaintext SSID/BSSID/IP are not emitted.

### Initial baseline behavior (current)

On startup:
1. Forces immediate public IP fetch attempt
2. Builds initial tick state
3. Initializes all debouncers from first snapshot
4. Emits `initial=true` events for all five signal types:
   - `VpnStateChanged`
   - `WifiLinkChanged`
   - `WifiSsidChanged`
   - `LocalNetworkChanged`
   - `PublicIpBucketChanged`

### Shutdown behavior

On cancellation, the collector exits the polling loop cleanly without emitting final state.

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

