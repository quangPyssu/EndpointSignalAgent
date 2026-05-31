# Signal Catalog

This document describes every `SignalEventType` emitted/handled by the agent, what it represents, and the emitted payload shape.

## Where signals are written

Signals are written as JSONL entries to:

- `spool/signals.jsonl` (current persisted stream)

Each line is a `SignalEvent`:

- `ts` (`DateTimeOffset`, UTC)
- `type` (`SignalEventType`, string enum)
- `payload` (`Dictionary<string,string>`)

> Note: The repo contains a design for `spool/raw_signals.jsonl` (`raw-collector-v1`). In the current workspace configuration, raw writing is disabled by default and can be enabled via `ESA_WRITE_RAW_SIGNALS=true`.

## Signal inventory

Legend (Kind):
- `Event`: point-in-time event
- `StateSample`: periodic sample of a state
- `StateChange`: emitted when a state changes
- `PreAggregated`: already aggregated over a time window

| SignalEventType | Producer | Kind | Typical cadence / trigger | Payload keys (string values) | Notes |
|---|---|---:|---|---|---|
| `Unknown` | n/a | Event | fallback | *(none defined)* | Used for parse/unknown cases. |
| `Heartbeat` | `HeartbeatSignalProvider` | Event | provider tick | `user`, `pid` | Lightweight liveness event. |
| `SessionLock` | `SessionStateCollector` | Event | state change (debounced) | `source`, `reason` | `source` is `WTS` or `SystemEvents`. |
| `SessionUnlock` | `SessionStateCollector` | Event | state change (debounced) | `source`, `reason` | See `SessionLock`. |
| `IdleSample` | `SessionStateCollector` | StateSample | bucket change (adaptive) | `idleMs`, `idleBucketSec`, `idleStatus`, `idlePollMode`, `expectedCadenceSec`, *(optional)* `userPresence`, `presenceSource`, *(optional)* `screensaverStatus` | `idleStatus` can be `ok`, `api_fail`, or `presence_update`. |
| `ScreenSaverOn` | `SessionStateCollector` | StateChange | state change (debounced) | `running`, `screensaverStatus` | `running` is `true`. |
| `ScreenSaverOff` | `SessionStateCollector` | StateChange | state change (debounced) | `running`, `screensaverStatus` | `running` is `false`. |
| `DisplayOn` | `SessionStateCollector` | StateChange | power event / initial synthetic | `displayState`, `source`, `confidence`, *(optional)* `reason`, *(optional)* `userPresence`, `presenceSource` | Startup emits a synthetic `DisplayOn` with `source=initial_unknown` and `confidence=low`. |
| `DisplayOff` | `SessionStateCollector` | StateChange | power event (debounced) | `displayState`, `source`, `confidence`, *(optional)* `userPresence`, `presenceSource` | `displayState` is `Off`. |
| `DisplayDimmed` | `SessionStateCollector` | StateChange | power event (debounced) | `displayState`, `source`, `confidence`, *(optional)* `userPresence`, `presenceSource` | `displayState` is `Dimmed`. |
| `ForegroundAppChanged` | `ApplicationUsageCollector` | Event | foreground switch commit (debounced) | `appKey`, `category`, `collectorMode`, `confidence` | `appKey` is a stable hash; category is derived from exe name. |
| `AppDwell` | `ApplicationUsageCollector` | Event | foreground slice ends | `appKey`, `category`, `durationMs`, `reason`, `dwellReason`, `collectorMode`, `confidence` | `reason`/`dwellReason` include `switch`, `no_foreground`, `shutdown_flush`. |
| `AppSwitchRate` | `ApplicationUsageCollector` | PreAggregated | every 60s window | `windowSec`, `switches`, `collectorMode` | `windowSec` is `60`. |
| `VpnStateChanged` | `NetworkContextCollector` | StateChange | debounced state change | `vpnOn`, `vpnAdapter`, `vpnConfidence`, `vpnReason`, *(optional)* `initial` | Startup emits `initial=true` snapshot. |
| `WifiLinkChanged` | `NetworkContextCollector` | StateChange | debounced state change | `wifiUp`, `wifiIdentityConfidence`, `wifiIdentityReason`, *(optional)* `initial` | Represents primary Wi‑Fi up/down. |
| `WifiSsidChanged` | `NetworkContextCollector` | StateChange | debounced identity change | `wifiSsid`, `wifiUp`, `wifiBssidHash`, `wifiIdentityConfidence`, `wifiIdentityReason`, *(optional)* `initial` | SSID/BSSID are hashed (privacy-preserving). |
| `LocalNetworkChanged` | `NetworkContextCollector` | StateChange | debounced fingerprint change | `localPrefix`, `localNetworkHash`, `localIpFamily`, `localPrefixHash`, `localNetworkReason`, *(optional)* `initial` | Values are hashed/coarsened (privacy-preserving). |
| `PublicIpBucketChanged` | `NetworkContextCollector` | StateChange | debounced bucket change | `publicIpBucket`, `publicIpAgeSeconds`, `publicIpFetchStatus`, *(optional)* `initial` | Public IP bucket is coarsened then hashed (IPv4 /24, IPv6 /48). |
| `SystemResourceTick` | `SystemResourceCollector` | StateSample | periodic (2s) | `cpu_available`, `cpu_pct`, `mem_available`, `mem_used_pct`, `mem_avail_mb`, `mem_total_mb`, `gpu_available`, `gpu_pct`, `gpu_mem_used_pct`, `gpu_engine_active_count`, `swap_available`, `net_rx_kbps`, `net_tx_kbps` | Best-effort sampling; availability flags indicate native API support. |
| `WifiSsidHash` | *(legacy enum value)* | Event | n/a | n/a | Defined in enum but not used by current collectors/aggregators. Use `WifiSsidChanged` instead. |

## Source references

- Signal enum: `src/Shared/Contracts/SignalEvent.cs`
- Collector behavior/payload docs: `docs/COLLECTORS.md`
- Aggregator consumption inventory: `docs/AGGREGATOR_SIGNAL_INVENTORY.md`
- Signal provenance/kind mapping: `src/SignalCollection/Contracts/RawCollectorSignalRecord.cs`
