# Agent Reading Guide (Top-Down)

This is the **fastest path** for an agent (or new maintainer) to understand how this repo works today.

## 1) Entry point and runtime shape

1. `src/Program.cs`
   - WinForms tray app entry point (`[STAThread]`).
   - Sets working directory to app base path.
   - Starts `TrayApplicationContext`.

2. `src/Tray/TrayApplicationContext.cs`
   - Builds and starts host via `AgentHostBootstrap.BuildHost(args)`.
   - Owns tray UX (status, pause/resume collection, export features, open spool, exit).
   - This class is the operational shell around all background services.

3. `src/Bootstrap/AgentHostBootstrap.cs`
   - Dependency injection registration and options validation.
   - Creates channels, broadcaster, providers, storage, and all hosted services.

---

## 2) Service graph (what runs)

Hosted services are mode-aware and registered in `AgentHostBootstrap`.

Always-on services:

1. `EnrollOnStartupService`
2. `SignalWriterService`
3. Collectors:
   - `SessionStateCollector`
   - `ApplicationUsageCollector`
   - `NetworkContextCollector`
   - `SystemResourceCollector`
4. Feature pipeline:
   - `FeatureExtractorService` (live extraction is forced off in DatasetCollection mode)
   - `KeyboardCommandService`

Normal mode only (`Agent:Mode=Normal`):
5. Send pipeline:
   - `BatchProducerService`
   - `BatchSendService`
6. Feature maintenance/upload:
   - `FeatureUploadService`
   - `FeatureCleanupService`
7. Status pipeline:
   - `StatusPollService`
   - `DecisionProcessorService`

DatasetCollection mode only (`Agent:Mode=DatasetCollection`):
- `CollectionSessionService`
- `AbnormalTaggingService`
- `ProgressTrackingService`
- `CollectionManifestService`
- `DatasetExportService`

---

## 3) Top-level data flow

### Signal fan-out

Collectors emit through `SignalCollectorBase.WriteSignalAsync(...)` into `ISignalBroadcaster`.

Broadcaster sends each signal to two channels:

- **Writer channel** -> `SignalWriterService`
- **Feature channel** -> `FeatureExtractorService`

### Persistence and transport

`SignalWriterService` writes each signal to:

- `spool/signals.jsonl` (legacy send-compatible format)
- `spool/raw_signals.jsonl` (canonical raw collector format)

In Normal mode, `BatchProducerService` reads from `spool/signals.jsonl` via `SpoolFileSignalProvider` and enqueues `SignalBatchRequest`.

In Normal mode, `BatchSendService` sends batches to backend.

In DatasetCollection mode, backend send/status/decision pipelines are not started.

### Feature extraction

`FeatureExtractorService` consumes the feature channel with event-time windowing and stores rows in SQLite:

- `spool/features.db`

### Status/decision

`StatusPollService` polls backend status and enqueues `StatusResponse`.

`DecisionProcessorService` consumes decisions via `IDecisionHandler`.

---

## 4) Canonical files to inspect per concern

### Boot/config

- `src/Bootstrap/AgentHostBootstrap.cs`
- `src/Bootstrap/Configuration/BackendOptions.cs`
- `src/Bootstrap/Configuration/AgentOptions.cs`
- `src/FeatureExtraction/Configuration/FeatureExtractorOptions.cs`
- `appsettings.json`

### Signal collection

- `src/SignalCollection/Collectors/*.cs`
- `src/SignalCollection/Broadcasting/SignalBroadcaster.cs`
- `src/SignalCollection/Services/SignalWriterService.cs`
- `src/SignalCollection/Storage/SpoolFileCollector.cs`
- `src/SignalCollection/Storage/RawSignalFileCollector.cs`

### Send pipeline

- `src/SignalCollection/Providers/SpoolFileSignalProvider.cs`
- `src/SignalCollection/Services/BatchProducerService.cs`
- `src/SignalCollection/Services/BatchSendService.cs`

### Feature pipeline

- `src/FeatureExtraction/Services/FeatureExtractorService.cs`
- `src/FeatureExtraction/SignalAggregator/*.cs`
- `src/FeatureExtraction/Storage/FeatureStore.cs`
- `src/FeatureExtraction/Services/FeatureUploadService.cs`
- `src/FeatureExtraction/Services/FeatureCleanupService.cs`
- `src/FeatureExtraction/Services/KeyboardCommandService.cs`

### Status/decision

- `src/Shared/Services/StatusPollService.cs`
- `src/Shared/Services/DecisionProcessorService.cs`
- `src/Shared/Handlers/DefaultDecisionHandler.cs`

---

## 5) Spool artifacts you should expect

Primary runtime artifacts under `spool/`:

- `enrollment.json`
- `signals.jsonl`
- `signals.offset`
- `raw_signals.jsonl`
- `features.db`

When running in DatasetCollection mode, expect additional manifests in `spool/manifests/`:

- `study_manifest.json`
- `participant_manifest.json`
- `session_<sessionId>.json`
- `session_<sessionId>.annotations.json`
- `progress_state.json`

And export folders under `exports/participant_<participantId>_<timestamp>/`.

---

## 6) Practical debugging order

When troubleshooting end-to-end behavior, inspect in this sequence:

1. Tray starts (`Program` + `TrayApplicationContext` logs).
2. Enrollment file (`spool/enrollment.json`) is created.
3. `signals.jsonl` and `raw_signals.jsonl` are growing.
4. `signals.offset` advances over time.
5. `features.db` receives rows.
6. Batch sender and status poll logs show backend interactions.

---

## 7) Non-obvious current behavior

- The app is a **WinForms tray application**, not a headless worker executable.
- Broadcast is explicit: one emitted signal is written to both writer and extractor channels.
- Feature extractor uses fixed schema window constants (`FeatureSchema.WindowSec=60`, `FeatureSchema.StepSec=30`) and warns if config differs.
- In `DatasetCollection` mode, backend is forced off and live extraction is forced off regardless of config values.

---

## 8) Related docs

- `docs/ARCHITECTURE.md` - runtime architecture and channels.
- `docs/COLLECTORS.md` - collector-level signal semantics.
- `docs/EXTRACTOR.md` - feature extraction, schema, storage.
- `docs/AGGREGATOR_SIGNAL_INVENTORY.md` - signal-to-feature mapping.

## 9) Per-module source references

Each `src/` subdirectory has a `README.md` with the file map, interfaces, data flow,
and invariants for that module:

- `src/Bootstrap/README.md` — DI wiring, options schema, identity, enrollment
- `src/SignalCollection/README.md` — collectors, broadcasting, spool storage, batch send
- `src/FeatureExtraction/README.md` — extractor, aggregators, feature store, CSV export
- `src/DatasetCollection/README.md` — sessions, tagging, progress, manifest storage, export
- `src/Shared/README.md` — contracts, decision handler, agent state, app categorizer
- `src/Tray/README.md` — tray shell, menu controls, exit sequence
