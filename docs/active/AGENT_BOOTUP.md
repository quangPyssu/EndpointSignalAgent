# Agent Bootup Sequence

Precise startup sequence for EndpointSignalAgent, from `Main` through all hosted services becoming ready.
Use this doc when debugging startup failures, tracing initialization order, or understanding why a service is (or isn't) running.

---

## Phase 1 — Process entry (`Program.cs`)

```
[STAThread] Main(args)
  1. Set Environment.CurrentDirectory = AppContext.BaseDirectory
     → all spool/ paths resolve relative to the exe directory
  2. ApplicationConfiguration.Initialize()
     → enables visual styles, sets compatible text rendering
  3. Application.Run(new TrayApplicationContext(args))
```

---

## Phase 2 — Tray shell construction (`TrayApplicationContext`)

Runs synchronously on the UI thread before any async work.

```
TrayApplicationContext(args)
  1. Create console LoggerFactory (SingleLine, timestamped, Information level)
  2. Capture SynchronizationContext (WindowsFormsSynchronizationContext)
  3. Allocate all ToolStripMenuItems — all disabled until host starts
  4. Create NotifyIcon — icon.ico, text = "EndpointSignalAgent (starting)", visible = true
  5. Wire ContextMenuStrip.Opening → RefreshMenuStateAsync
  6. Wire DoubleClick → ShowStatus
  7. Fire-and-forget: _ = StartHostAsync(args)
     → returns immediately; host builds and starts asynchronously
```

The tray is visible and responsive before the host starts.

---

## Phase 3 — DI wiring (`AgentHostBootstrap.BuildHost`)

Called from `StartHostAsync`. Creates the `IHost` but does not start services yet.

```
BuildHost(args)
  1. Host.CreateApplicationBuilder(args)
     → reads appsettings.json, appsettings.{Environment}.json, environment variables, args

  2. Read Agent:Mode from config
     → default: "DatasetCollection"
     → throws InvalidOperationException immediately if value is not "Normal" or "DatasetCollection"

  3. Compute derived flags (determined once, never change):
     isDatasetMode           = Mode == "DatasetCollection"
     liveExtractionEnabled   = FeatureExtractor:Enabled
                               && FeatureExtractor:EnableLiveExtraction
                               && !isDatasetMode

  4. Register and validate options (ValidateOnStart — host.Build() will throw on violation):
     BackendOptions       ← section "Backend"
       PostConfigure: if isDatasetMode → UseBackend = false
       Validate: BaseUrl must be absolute URI when UseBackend=true
     AgentOptions         ← section "Agent"
       PostConfigure: empty Mode → defaults to "DatasetCollection"
       Validate: Mode valid, DecisionQueueCapacity 10–100000, StatusPollSeconds 1–3600
     FeatureExtractorOptions ← section "FeatureExtractor"
       PostConfigure: if isDatasetMode → EnableLiveExtraction = false
       Validate: WindowSizeSeconds 10–3600, WindowSlideSeconds 5–3600, MaxEventsPerWindow 100–100000
     DatasetCollectionOptions ← section "DatasetCollection"
       Validate: DailyActiveHourTarget >= 0, WeeklyActiveDayTarget 0–7, StudyWeekTarget >= 0

  5. Create channels (singletons, shared directly via closure):
     signalWriterChannel      = BoundedChannel<BroadcastSignal>(1000, Wait, multi-writer, single-reader)
     featureExtractorChannel  = BoundedChannel<BroadcastSignal>(1000, Wait, multi-writer, single-reader)
     decisionChannel          = BoundedChannel<StatusResponse>(DecisionQueueCapacity, DropOldest)
       → decisionChannel registered as singleton in DI

  6. Register SignalBroadcaster:
     writers = [ signalWriterChannel.Writer ]
     if liveExtractionEnabled: writers += featureExtractorChannel.Writer
     → ISignalBroadcaster singleton

  7. Register channel readers as singletons:
     ISignalWriterChannelReader   → wraps signalWriterChannel.Reader
     IFeatureExtractorChannelReader → wraps featureExtractorChannel.Reader

  8. Register singletons:
     IAgentIdentity        → AgentIdentity (lazy, reads enrollment on first access)
     EnrollmentStore       → IEnrollmentStore (spool/enrollment.json)
     ISignalProvider       → SpoolFileSignalProvider (spool/signals.jsonl + spool/signals.offset)
     ICollectionControl    → CollectionControl
     IFeatureStore         → FeatureStore (spool/features.db)
     FeatureExtractorService → also added as IHostedService

  9. Register HTTP clients:
     BackendClient (typed) and "BackendClient" (named)
     → configured with BaseUrl + Timeout only when UseBackend=true

  10. Register hosted services — always-on:
      EnrollOnStartupService
      SignalWriterService
      SessionStateCollector
      ApplicationUsageCollector
      NetworkContextCollector
      SystemResourceCollector
      FeatureExtractorService      (singleton, also hosted)
      FeatureCsvStreamService
      FeatureCleanupService
      DeviceGuardService           (self-disables if DeviceGuard:Enabled=false)

  11a. Normal mode only — additional hosted services:
       IAgentState         → AgentState
       IDecisionHandler    → DefaultDecisionHandler
       StatusPollService
       DecisionProcessorService

  11b. DatasetCollection mode only — singletons:
       SessionManifestStore
       AnnotationStore
       ProgressStateStore
       ICollectionManifestService  → CollectionManifestService
       ICollectionSessionService   → CollectionSessionService
       IAbnormalTaggingService     → AbnormalTaggingService
       IProgressTrackingService    → ProgressTrackingService
       IDatasetShutdownCoordinator → DatasetShutdownCoordinator
       IDatasetRecoveryService     → DatasetRecoveryService
       DatasetExportService        (singleton, not hosted)

       Additional hosted services (DatasetCollection only):
       ProgressTrackingService     (cast from IProgressTrackingService)
       DatasetShutdownHooksService
       DatasetSessionStartupService

  12. builder.Build() → validates all ValidateOnStart options; throws on any violation
```

---

## Phase 4 — Host startup (`StartHostAsync` continues)

```
await _host.StartAsync()
  → .NET host calls IHostedService.StartAsync() in registration order

Startup order (always-on services):
  1.  EnrollOnStartupService
        → if spool/enrollment.json missing: POST /enroll to backend (Normal) or generate local id (DatasetCollection)
        → writes spool/enrollment.json
        → unblocks EnrollmentStore.GetIdAsync() waiters

  2.  SignalWriterService
        → opens spool/signals.jsonl and spool/raw_signals.jsonl for append
        → begins consuming signalWriterChannel (blocks until signals arrive)

  3.  SessionStateCollector        → polls WTS session state on timer
  4.  ApplicationUsageCollector    → polls foreground window on timer
  5.  NetworkContextCollector      → polls network adapter state on timer
  6.  SystemResourceCollector      → polls CPU, memory, disk on timer

  7.  FeatureExtractorService
        → if liveExtractionEnabled: opens features.db (WAL mode), begins consuming featureExtractorChannel
        → if disabled: drains featureExtractorChannel to /dev/null (keeps channel healthy)

  8.  FeatureCsvStreamService
        → Normal mode: streams unsent feature rows to backend /features/row
        → DatasetCollection mode: no-op drain (backend is forced off)

  9.  FeatureCleanupService
        → starts 1h periodic timer; prunes >500 unsent rows and >7-day old sent rows

  10. DeviceGuardService
        → if DeviceGuard:Enabled=false: logs and exits immediately
        → if enabled: starts idle-poll loop (P/Invoke GetLastInputInfo)

Normal-mode additional services (steps 11–12):
  11. StatusPollService
        → polls backend /status on interval; writes StatusResponse to decisionChannel
  12. DecisionProcessorService
        → consumes decisionChannel; delegates to IDecisionHandler

DatasetCollection-mode additional services (steps 11–13):
  11. ProgressTrackingService
        → loads progress_state.json; starts periodic recalculation timer
  12. DatasetShutdownHooksService
        → registers ApplicationStopping callback to flush open sessions on graceful stop
  13. DatasetSessionStartupService
        → calls IDatasetRecoveryService.RecoverDanglingSessionsAsync()
             → reads session_*.json manifests; closes any with State="Running" or "Paused" that are stale
        → if SessionAutoStart=true:
             calls ICollectionSessionService.StartSessionAsync(label="", notes="auto_started=true", initiatedBy="startup")
             calls IProgressTrackingService.RecalculateAsync()
             logs: "Dataset session auto-started at host startup: {SessionId}"
        → if SessionAutoStart=false: no session started; operator must use tray menu
```

---

## Phase 5 — Tray activation

After `_host.StartAsync()` returns:

```
1. _hostRunning = true
2. Resolve service references from DI:
   _collectionControl, _collectionSessionService, _abnormalTaggingService,
   _progressTrackingService, _datasetExportService, _datasetShutdownCoordinator,
   _datasetOptions, _agentOptions
3. Enable tray menu items:
   _pauseResumeMenuItem.Enabled = true   (always)
   _progressMenuItem.Enabled = datasetMode
   _exportDatasetMenuItem.Enabled = datasetMode
   _openManifestFolderMenuItem.Enabled = datasetMode
4. Log: "Host started"
5. UpdatePauseMenuText()
6. RefreshMenuStateAsync()   → sets session/abnormal menu item enabled states
7. UpdateDatasetSessionStatusAsync()
   → sets tray tooltip: "EndpointSignalAgent (DatasetCollection, session:running)"
     or "EndpointSignalAgent (DatasetCollection, session:inactive)"
     or "EndpointSignalAgent (Normal)"
```

---

## Startup failure handling

```
catch (Exception ex) in StartHostAsync:
  → LogCritical("Host startup failed")
  → MessageBox.Show with ex.Message
  → calls ExitAsync("startup_error")
     → hides tray icon
     → _host.StopAsync(15s) if partially started
     → _host.Dispose()
     → ExitThread() → terminates WinForms message loop → process exits
```

Common startup failures:
- **Options validation** — invalid `Agent:Mode`, missing `Backend:BaseUrl` when `UseBackend=true`, out-of-range numeric options
- **Missing `icon.ico`** — `new Icon(...)` throws at tray construction (before async)
- **Enrollment failure in Normal mode** — `EnrollOnStartupService` throws if backend POST fails and no cache

---

## Spool artifacts after successful startup

```
spool/
  enrollment.json         ← written by EnrollOnStartupService (step 1)
  signals.jsonl           ← opened by SignalWriterService (step 2), grows as collectors emit
  signals.offset          ← created by SpoolFileSignalProvider when Normal mode reads spool
  raw_signals.jsonl       ← opened alongside signals.jsonl (env var ESA_WRITE_RAW_SIGNALS or always-on)
  features.db             ← opened by FeatureExtractorService (step 7) when live extraction enabled

spool/manifests/          ← DatasetCollection mode only
  study_manifest.json
  participant_manifest.json
  progress_state.json
  session_<id>.json       ← created when a session starts
  session_<id>.annotations.json
```

---

## Key invariants

- **CWD is fixed at startup** — `Environment.CurrentDirectory = AppContext.BaseDirectory` in `Main`. All relative paths (`spool/`, `exports/`) resolve from the exe directory.
- **Mode is immutable** — read once from config in `BuildHost`, never re-read at runtime.
- **DatasetCollection mode overrides are applied at registration time**, not lazily — `UseBackend=false` and `EnableLiveExtraction=false` are forced via `PostConfigure` before any service starts.
- **Options validation runs before host start** — `ValidateOnStart` causes `builder.Build()` to throw on bad config; no service `StartAsync` is ever called if config is invalid.
- **`EnrollmentStore.GetIdAsync()` blocks** until `spool/enrollment.json` exists — any service that reads `IAgentIdentity.DeviceId` before `EnrollOnStartupService` finishes will wait.
- **Signal channels use `FullMode = Wait`** — a stalled consumer (`SignalWriterService` or `FeatureExtractorService`) will back-pressure all collectors. The channel drains should never be blocked for extended periods.
