# Tray

WinForms tray application shell. Owns the `IHost` lifetime, the system tray icon,
and all operator-facing UI controls.

## Files

### `TrayApplicationContext.cs`

`ApplicationContext` subclass — the WinForms application context that drives the process.

**Startup sequence:**
1. `Program.Main` → `new TrayApplicationContext(args)`.
2. Constructor builds all tray menu items and `NotifyIcon`.
3. Fires `_ = StartHostAsync(args)` (fire-and-forget).
4. `StartHostAsync` calls `AgentHostBootstrap.BuildHost(args)`, starts the host, resolves
   service references from DI container.

Services resolved from DI after host start:
- `ICollectionControl` — pause/resume collection.
- `KeyboardCommandService` — triggers feature export operations.
- `ICollectionSessionService` — session start/pause/resume/end.
- `IAbnormalTaggingService` — start/end/mark-last-5-min abnormal.
- `IProgressTrackingService` — progress queries.
- `DatasetExportService` — participant package export.
- `IDatasetShutdownCoordinator` — graceful finalization.

**Tray menu structure:**
```
[Status]
─────────────────
[Collection]
  ├── Pause/Resume collection
  ├── Open spool folder
  └── Open manifest folder     ← DatasetCollection mode only
[Session]                      ← DatasetCollection mode only
  ├── Start session
  ├── Pause session
  ├── Resume session
  ├── End session
  └── Enter short note
[Abnormal]                     ← DatasetCollection mode only
  ├── Start abnormal segment
  ├── End abnormal segment
  └── Mark last 5 min abnormal
[Progress]                     ← DatasetCollection mode only
  ├── [progress bar]
  ├── Completion: X%
  ├── Status: ...
  ├── Total collected: Xh Xm
  ├── Active time: Xh Xm
  ├── Sessions: X
  ├── Days: X
  ─────────────────
  └── Open progress details...
[Export]
  ├── Export dataset package   ← DatasetCollection mode only
  └── Export all features to CSV
─────────────────
[Exit]
```

**Progress menu:** Opens with `DropDownOpening` event which calls `RefreshProgressMenuAsync`.
Progress bar value maps `CompletionRatio` (0–1) to 0–1000 integer scale.

**Status dialog (`ShowStatus`):** Shows host running state, mode, session state, enrollment file,
backend state, and full paths of four spool files.

**Session start flow:**
1. Prompts user for label and notes via `Prompt()` (inline WinForms form).
2. Calls `CollectionSessionService.StartSessionAsync`.
3. Calls `ProgressTrackingService.RecalculateAsync`.
4. Shows session ID in a message box.

**Abnormal segment flow (start/end):**
1. Prompts user to select a scenario code from the predefined list.
2. Prompts for an optional short note.
3. Calls `AbnormalTaggingService.StartAbnormalSegmentAsync` or `EndAbnormalSegmentAsync`.

**Mark last 5 min abnormal:** calls `MarkLastMinutesAbnormalAsync(5, ...)`.

**Export all features:** delegates to `KeyboardCommandService.ExportAllFeatureDataAsync`.
Same code path as tray keyboard shortcut `Ctrl+O`.

**Export dataset package:** calls `DatasetExportService.ExportParticipantPackageAsync`
with `DatasetCollectionOptions.ParticipantId`.

**Exit sequence (`ExitAsync`):**
1. Sets `_exiting = 1` via `Interlocked.Exchange` (idempotent).
2. Hides tray icon.
3. Calls `IDatasetShutdownCoordinator.FinalizeAsync` (8 s timeout).
4. Calls `_host.StopAsync` (15 s timeout).
5. Disposes host and logger factory.
6. Calls `ExitThread()`.

**Thread model:** All menu event handlers run on the WinForms UI thread (STA).
`async` handlers are fire-and-forget from tray events — exceptions are caught and shown
via `MessageBox`. `_uiContext` is captured in constructor for cross-thread `Post` calls.

**Tray icon text:** limited to 63 characters. Format:
- Normal mode: `EndpointSignalAgent (Normal)`
- DatasetCollection mode: `EndpointSignalAgent (DatasetCollection, session:<state>)`

**Debug console:** `Program.Main` has `bool enableConsoleDebug = true` which calls
`AllocConsole()`. Set to `false` to suppress the console window in production builds.

## Invariants

- `ExitAsync` is idempotent — double-calls are no-ops due to `Interlocked.Exchange` guard.
- Menu items for DatasetCollection-specific operations are `Enabled=false` in Normal mode and
  only enabled after `StartHostAsync` confirms the mode.
- `_keyboardCommandService` being null disables the "Export all features to CSV" menu item.
- `Prompt()` is synchronous (modal WinForms form) — must only be called on the UI thread.
