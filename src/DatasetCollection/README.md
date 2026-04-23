# DatasetCollection

Manages research study sessions, abnormal behavior tagging, progress tracking, and
participant data export. Active only in `Agent:Mode=DatasetCollection`.

## Conceptual model

```
Study
  └── Participant (ParticipantId)
        └── Sessions (0..n)
              ├── State: Idle → Running → Paused → Completed
              └── Annotations (0..n)
                    ├── State: open (StartedAtUtc set, EndedAtUtc null)
                    └── State: complete (IsComplete=true, EndedAtUtc set)
```

## Data storage

All manifests are JSON files in `spool/manifests/` (default; configured by `ManifestRoot`).
Atomic writes via `AtomicJsonFileWriter` (write-to-temp + rename).

```
spool/manifests/
  study_manifest.json
  participant_manifest.json
  session_<sessionId>.json
  session_<sessionId>.annotations.json
  progress_state.json
```

## Files

### `Abstractions/`

Interfaces for all major DatasetCollection services:

- `ICollectionSessionService` — start / pause / resume / end / list sessions.
- `IAbnormalTaggingService` — start/end/mark-last-N-min abnormal segments.
- `IProgressTrackingService` — recalculate and query study progress.
- `ICollectionManifestService` — save/load all manifest JSON files.
- `IDatasetShutdownCoordinator` — orchestrate graceful finalization on exit.
- `IDatasetRecoveryService` — close stale sessions from previous runs.

### `Services/CollectionSessionService.cs`

Implements `ICollectionSessionService`. Gate: `SemaphoreSlim(1,1)`.

Session lifecycle:
- `StartSessionAsync` — creates `CollectionSessionRecord` (state=`Running`), saves via manifest service, writes study/participant manifests.
- `PauseSessionAsync` / `ResumeSessionAsync` — call `TransitionAsync` (state=`Paused`/`Running`).
- `EndSessionAsync` — call `TransitionAsync` (state=`Completed`, sets `EndedAtUtc`); clears `CurrentSession`.
- `CloseStaleSessionsAsync` — scans all sessions for open state without `EndedAtUtc`; transitions them to `Completed`. Used by recovery service on startup.

`CurrentSession` — in-memory latest active session. Null when no session is running.
Notes are merged with pipe separator (`" | "`).

### `Services/AbnormalTaggingService.cs`

Implements `IAbnormalTaggingService`. Gate: `SemaphoreSlim(1,1)`.

In-memory cache of annotations per session ID.

- `StartAbnormalSegmentAsync` — creates open `AbnormalAnnotationRecord` (IsComplete=false).
  Idempotent: returns existing open annotation if one exists. Requires active session.
- `EndAbnormalSegmentAsync` — closes the open annotation (IsComplete=true, EndedAtUtc=now).
- `MarkLastMinutesAbnormalAsync` — creates a complete, retrospective annotation
  `[now - N minutes, now]` with `IsComplete=true` (no open state needed).
- `GetActiveAnnotationAsync` — returns current open annotation or null.

Scenario codes used in tray: `DIFFERENT_USER`, `UNUSUAL_APP_SEQUENCE`,
`UNUSUAL_TIME_CONTEXT`, `NETWORK_CONTEXT_SHIFT`, `LOW_ACTIVITY_HIDE`,
`REMOTE_ACCESS_CONTEXT`, `SCRIPTED_SIMULATION_OTHER`.

### `Services/ProgressTrackingService.cs`

Implements `IProgressTrackingService` and `BackgroundService`. Runs every 60 s when
`EnableProgressTracking=true`.

`RecalculateAsync` computes `ProgressStateRecord` from all sessions and annotations:
- `validCollectionDays` — distinct dates of completed sessions ≥ `MinSessionMinutes`.
- `runtimeHours` — sum of (EndedAtUtc - StartedAtUtc) for completed sessions.
- `activeHours` — `runtimeHours * 0.85` minus 0.1 if currently paused.
- `abnormalMinutes` — sum of complete annotation durations.
- `abnormalScenarios` — count of distinct `ScenarioCode` in complete annotations.
- `studySpanWeeks` — floor((now - earliest session start) / 7 days).
- `completionRatio` — average of six threshold ratios (each clamped 0–1).

Completion thresholds: `StudyWeekTarget`, `WeeklyActiveDayTarget × StudyWeekTarget`,
`ExpectedSessionCount`, `DailyActiveHourTarget × WeeklyActiveDayTarget × StudyWeekTarget`,
`ExpectedAbnormalScenarioCount`, `ExpectedAbnormalMinutesMin`.

`GetTraySnapshotAsync` returns the trimmed `ProgressTraySnapshot` for tray menu display.

### `Services/CollectionManifestService.cs`

Implements `ICollectionManifestService`. Thin wrapper around the three stores.

Delegates:
- `SaveSessionAsync` / `LoadSessionsAsync` → `SessionManifestStore`
- `SaveAnnotationsAsync` / `LoadAnnotationsAsync` → `AnnotationStore`
- `SaveProgressAsync` / `LoadProgressAsync` → `ProgressStateStore`
- `SaveStudyManifestAsync` / `SaveParticipantManifestAsync` → `AtomicJsonFileWriter` directly.

### `Services/DatasetExportService.cs`

Creates participant package directories under `ExportRoot`.

Export sequence:
1. Create `exports/participant_<id>_<yyyyMMdd_HHmmss>/`.
2. Copy `spool/raw_signals.jsonl` (if present).
3. Copy all `*.json` from `ManifestRoot` (top-level only).
4. Write `progress_snapshot.json` (current progress state).
5. Write `collector_health_snapshot.json` (running flags all true at export time; `collectionPaused` reflects current state).
6. Compute SHA-256 for all files added so far.
7. Write `dataset_export_manifest.json` (checksums, agent version, schema `"dataset-collection-v1"`).

Note: `dataset_export_manifest.json` itself is NOT included in its own checksums map.

### `Services/DatasetSessionStartupService.cs`

`BackgroundService`. Runs once on host start:
- Calls `IDatasetRecoveryService.RecoverStaleSessionsAsync` to close unclosed sessions from previous runs.
- If `SessionAutoStart=true` and no current session is running, starts a new session with auto-generated label.

### `Services/DatasetShutdownCoordinator.cs` / `DatasetShutdownHooksService.cs`

`IDatasetShutdownCoordinator.FinalizeAsync(reason, ct)` — called from `TrayApplicationContext.ExitAsync`
with 8 s timeout. Closes any open abnormal annotation and ends the current session.

`DatasetShutdownHooksService` — `BackgroundService` that calls `FinalizeAsync` when the host
cancellation token fires (covers non-tray shutdown paths).

### `Services/DatasetRecoveryService.cs`

Calls `CollectionSessionService.CloseStaleSessionsAsync` on startup. Returns count of sessions recovered.

### `Storage/SessionManifestStore.cs`

Reads/writes `session_<sessionId>.json` files via `AtomicJsonFileWriter`.
`LoadSessionsAsync` globs all matching files in `ManifestRoot`.

### `Storage/AnnotationStore.cs`

Reads/writes `session_<sessionId>.annotations.json` files via `AtomicJsonFileWriter`.

### `Storage/ProgressStateStore.cs`

Reads/writes `progress_state.json` via `AtomicJsonFileWriter`.

### `Storage/AtomicJsonFileWriter.cs`

Writes JSON to a `.tmp` file then `File.Move(..., overwrite: true)` — prevents partial reads
during write. Uses `JsonSerializerOptions.Web` with `WriteIndented=true`.

## Contracts

| Record | File | Key fields |
|---|---|---|
| `CollectionSessionRecord` | `Contracts/CollectionSessionRecord.cs` | SessionId, State, StartedAtUtc, EndedAtUtc, Notes |
| `AbnormalAnnotationRecord` | `Contracts/AbnormalAnnotationRecord.cs` | AnnotationId, SessionId, ScenarioCode, StartedAtUtc, EndedAtUtc, IsComplete |
| `ProgressStateRecord` | `Contracts/ProgressStateRecord.cs` | CompletionRatio, CompletionStatus, TotalSessionsCompleted |
| `ProgressTraySnapshot` | `Contracts/ProgressTraySnapshot.cs` | Compact form for tray display |
| `DatasetExportManifest` | `Contracts/DatasetExportManifest.cs` | Checksums map, SchemaVersion |
| `CollectorHealthSnapshot` | `Contracts/CollectorHealthSnapshot.cs` | Running flags + pause state |

## Invariants

- `CurrentSession` in `CollectionSessionService` is the in-memory authoritative session state.
  Manifests on disk are the persistent authoritative state. Recovery reads from disk on startup.
- `AbnormalTaggingService` caches annotations in memory per session. Cache is populated lazily
  from disk. Not invalidated externally — safe because only one writer (single-process).
- Export does NOT end the current session. It is safe to export mid-study.
- `ManifestRoot` and `ExportRoot` are relative to the application's working directory
  (`AppContext.BaseDirectory`).
