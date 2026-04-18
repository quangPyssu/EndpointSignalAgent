# EndpointSignalAgent Architecture (Current)

## Overview

EndpointSignalAgent is a **Windows tray application** that hosts a .NET background-service graph. The tray shell (`TrayApplicationContext`) starts and stops the host and exposes operator controls (status, pause/resume collection, feature export, open spool, exit).

Core runtime flows:

1. Enrollment (identity bootstrap)
2. Signal collection and broadcast fan-out
3. Disk spool + backend send pipeline
4. Feature extraction + feature upload/cleanup
5. Status polling + decision processing

---

## Entry points and bootstrap

- Process entry: `src/Program.cs`
- Tray shell: `src/Tray/TrayApplicationContext.cs`
- DI/service wiring: `src/Bootstrap/AgentHostBootstrap.cs`

`Program.Main` sets current working directory to the app base directory, initializes WinForms, and runs the tray context.

---

## Runtime topology

```text
Collectors (Session, AppUsage, NetworkContext, SystemResource)
        │
        ▼
SignalCollectorBase.WriteSignalAsync(...)
        │
        ▼
ISignalBroadcaster
  ├──► Writer channel ─► SignalWriterService ─► spool/signals.jsonl + spool/raw_signals.jsonl
  └──► Feature channel ─► FeatureExtractorService ─► spool/features.db

spool/signals.jsonl ─► SpoolFileSignalProvider ─► BatchProducerService ─► Channel<SignalBatchRequest> ─► BatchSendService ─► Backend /send

Backend /status ─► StatusPollService ─► Channel<StatusResponse> ─► DecisionProcessorService ─► IDecisionHandler

spool/features.db ─► FeatureUploadService ─► Backend /features
```

---

## Channels

Configured in `AgentHostBootstrap`:

1. **Broadcast channels** (`BroadcastSignal`), capacity 1000 each
   - `BoundedChannelFullMode.Wait`
   - One dedicated reader per channel:
     - writer channel -> `SignalWriterService`
     - feature channel -> `FeatureExtractorService`

2. **Outgoing send queue** (`Channel<SignalBatchRequest>`)
   - Capacity: `Agent:OutgoingQueueCapacity`
   - Full mode: `DropOldest`
   - Single writer/reader

3. **Decision queue** (`Channel<StatusResponse>`)
   - Capacity: `Agent:DecisionQueueCapacity`
   - Full mode: `DropOldest`
   - Single writer/reader

---

## Hosted services

Registered hosted services:

- `EnrollOnStartupService`
- `SignalWriterService`
- `SessionStateCollector`
- `ApplicationUsageCollector`
- `NetworkContextCollector`
- `SystemResourceCollector`
- `BatchProducerService`
- `BatchSendService`
- `FeatureExtractorService`
- `FeatureUploadService`
- `FeatureCleanupService`
- `KeyboardCommandService`
- `StatusPollService`
- `DecisionProcessorService`

---

## Persistence model

Spool/data artifacts in `spool/`:

- `enrollment.json` - enrollment cache
- `signals.jsonl` - send-compatible spool format
- `signals.offset` - byte-offset cursor for spool reading
- `raw_signals.jsonl` - canonical raw collector export (`raw-collector-v1` envelope)
- `features.db` - SQLite feature store

---

## Configuration and validation

### `Backend` (`BackendOptions`)

- `UseBackend`
- `BaseUrl` (required absolute URL when backend enabled)
- `EnrollPath`, `SendPath`, `StatusPath`, `FeaturesPath`
- `TimeoutSeconds`

### `Agent` (`AgentOptions`)

- `OutgoingQueueCapacity` (10..100000)
- `DecisionQueueCapacity` (10..100000)
- `DefaultReportSeconds` (1..3600)
- `StatusPollSeconds` (1..3600)

### `FeatureExtractor` (`FeatureExtractorOptions`)

- `Enabled`
- `EnableLiveExtraction`
- `WindowSizeSeconds` (10..3600)
- `WindowSlideSeconds` (5..3600)
- `MaxEventsPerWindow` (100..100000)

Note: live extractor currently uses fixed schema constants from `FeatureSchema` (60s/30s) and logs a warning if config values differ.

---

## Operator surfaces

Tray menu operations from `TrayApplicationContext`:

- Status dialog
- Export all features to CSV (same path as Ctrl+O flow)
- Open spool folder
- Pause/resume collection (`ICollectionControl`)
- Exit (graceful host stop)

Keyboard command service supports extractor operations (Ctrl+E / Ctrl+P / Ctrl+O / Ctrl+Shift+X).

---

## Recommended reading order for contributors

1. `docs/AGENT_GUIDE.md`
2. `src/Bootstrap/AgentHostBootstrap.cs`
3. `src/Tray/TrayApplicationContext.cs`
4. `docs/COLLECTORS.md`
5. `docs/EXTRACTOR.md`
6. `docs/AGGREGATOR_SIGNAL_INVENTORY.md`
