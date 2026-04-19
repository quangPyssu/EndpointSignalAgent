# EndpointSignalAgent

Windows tray application that collects endpoint signals, writes local spool artifacts, extracts windowed features, and optionally sends data to backend services.

## Runtime modes

- `Agent:Mode=Normal`
  - Backend send/status/decision pipelines are enabled when `Backend:UseBackend=true`.
  - Live feature extraction follows `FeatureExtractor:EnableLiveExtraction`.
- `Agent:Mode=DatasetCollection`
  - Backend is forced off (`Backend:UseBackend=false`).
  - Live extraction is forced off (`FeatureExtractor:EnableLiveExtraction=false`).
  - Dataset services are enabled for session lifecycle, abnormal tagging, progress tracking, and package export.

## Start here (for contributors/agents)

1. Read **`docs/AGENT_GUIDE.md`** for a top-down map of runtime flows and key files.
2. Read **`docs/ARCHITECTURE.md`** for DI wiring, channels, and hosted services.
3. Use domain docs as needed:
   - `docs/COLLECTORS.md`
   - `docs/EXTRACTOR.md`
   - `docs/AGGREGATOR_SIGNAL_INVENTORY.md`

## Running as a Windows tray app

1. Build and run:
   ```powershell
   dotnet run --project .\EndpointSignalAgent.csproj
   ```
2. The app starts in the background and shows a tray icon.
3. Right-click the tray icon for:
   - **Status**
   - **Export all features to CSV** (same behavior as Ctrl+O)
   - **Open spool folder**
   - **Open manifest folder** (DatasetCollection mode)
   - **Pause collection / Resume collection**
   - DatasetCollection actions:
     - **Start/Pause/Resume/End collection session**
     - **Start/End abnormal segment**
     - **Mark last 5 min abnormal**
     - **Enter short note**
     - **Show progress**
     - **Export dataset package**
   - **Exit**
4. Use **Exit** for graceful shutdown of hosted services.

## Runtime artifacts (`spool/`)

- `enrollment.json`
- `signals.jsonl`
- `signals.offset`
- `raw_signals.jsonl`
- `features.db`

In `DatasetCollection` mode, additional artifacts are written under `spool/manifests`:

- `study_manifest.json`
- `participant_manifest.json`
- `session_<sessionId>.json`
- `session_<sessionId>.annotations.json`
- `progress_state.json`

Dataset package export output is written under `exports/participant_<participantId>_<timestamp>/`.

## Smooth setup for "start at user logon"

### Option A (recommended for distribution/export): one-click scripts beside the app

When you export/publish the app, include these files next to `EndpointSignalAgent.exe`:
- `install-logon-startup.cmd`
- `remove-logon-startup.cmd`
- (and their `.ps1` counterparts)

> Note: startup registration scripts configure the task working directory to the app folder, so spool/features files are created next to the executable instead of `C:\Windows\System32`.

Then users can simply double-click:
- `install-logon-startup.cmd` to register startup
- `remove-logon-startup.cmd` to unregister startup

### Option B (from repo): direct scripts

Register startup task:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\register-logon-startup.ps1 -ExecutablePath "C:\path\to\EndpointSignalAgent.exe"
```

Unregister startup task:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\unregister-logon-startup.ps1
```

## Export/publish with startup scripts included

Use this helper to publish and automatically copy setup/remove scripts into the export folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\export-app-with-startup-tools.ps1
```

Optional example with self-contained output:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\export-app-with-startup-tools.ps1 -Runtime win-x64 -SelfContained
```
