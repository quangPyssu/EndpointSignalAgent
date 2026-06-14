# Docs Audit & History Update — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring all `docs/active/` files in sync with the codebase as of 2026-06-14 (FeatureVersion 1.2.2, AppFocusHeartbeat feature) and append Phase 8 to the development history.

**Architecture:** Documentation-only changes — no code touched. Active docs live in `docs/active/`. History document lives in `docs/history/`. Deprecated plans are in `docs/decrecated/`. The repo also has an uncommitted reorganization (files moved from `docs/` root to subdirs) that should be committed together with the doc updates.

**Tech Stack:** Markdown, git

---

## What Changed Since Last Docs Sweep (2026-06-10)

Commits after `32b1b19` (last full docs sweep):

| Commit | Date | What |
|---|---|---|
| `24da8a6` | Jun 10 | fix: drain empty sleep |
| `c774f40` | Jun 10 | chore: sleep data recover |
| `0388759`/`ffd1a6f` | Jun 11 | wip: some report |
| `b590e58` | Jun 11 | docs: document AppFocusHeartbeat + open-dwell synthesis (code already in) |
| `e749337` | Jun 11 | feat: bump FeatureVersion to 1.2.2 for AppFocusHeartbeat open-dwell fix |
| `cc0d4f2` | Jun 11 | Merge Data_Recovery into deployment_prep |
| `c5097a2` | Jun 11 | feat: AppDwellReplayPreprocessor — inject synthetic heartbeats from raw history |
| `ff67700` | Jun 11 | feat: inject synthetic AppFocusHeartbeats in file replay path |
| `52495ba`/`e098261`/`9c0498f` | Jun 12-14 | wip |
| `12421af` | Jun 14 | chore: docs (reorganization into active/decrecated/history/Thesis subdirs) |

## Gaps Found in Active Docs

| File | Issue |
|---|---|
| `EXTRACTOR.md` | `FeatureVersion = "1.2.1"` — must be `1.2.2` |
| `COLLECTORS.md` | Missing `AppFocusHeartbeat` in ApplicationUsageCollector signals section |
| `AGENT_GUIDE.md` | Normal mode still lists `BatchProducerService`, `BatchSendService`, `FeatureUploadService`, `KeyboardCommandService` — all removed in Phase 6 |
| `AGENT_GUIDE.md` | Doc paths use `docs/X.md` format — now `docs/active/X.md` after reorganization |
| `ARCHITECTURE.md` | Recommended reading order paths use `docs/X.md` — now `docs/active/X.md` |
| `DATASET_EXPORT_OUTPUTS.md` | Missing `AppFocusHeartbeat`, `PowerSuspend`, `PowerResume`, `CollectionGapDetected` from `raw_signals.jsonl` signal type list |
| `FEATURE_SCHEMA.md` | App features section lists `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate` as sources but not `AppFocusHeartbeat` (open-dwell synthesis) |
| `docs/history/` | Development history only covers up to Jun 10; Phase 8 (AppFocusHeartbeat, v1.2.2, replay fix) not recorded |

---

### Task 1: Fix `EXTRACTOR.md` — FeatureVersion 1.2.2

**Files:**
- Modify: `docs/active/EXTRACTOR.md`

- [ ] **Step 1: Update FeatureVersion string**

In `EXTRACTOR.md`, change every occurrence of `1.2.1` to `1.2.2`.

Lines to change:
- `FeatureVersion = "1.2.1"` → `FeatureVersion = "1.2.2"`
- `"1.2.1"` wherever it appears in the FeatureRow fields description

---

### Task 2: Update `COLLECTORS.md` — Add AppFocusHeartbeat

**Files:**
- Modify: `docs/active/COLLECTORS.md`

- [ ] **Step 1: Add AppFocusHeartbeat to ApplicationUsageCollector signals list**

In the `ApplicationUsageCollector` signals emitted section, add `AppFocusHeartbeat` after `AppSwitchRate`.

- [ ] **Step 2: Add AppFocusHeartbeat signal emission details**

After the `AppSwitchRate` emission details block, add:

```markdown
#### `AppFocusHeartbeat`
Emitted every `15s` while a dwell is open (a foreground app is focused).

Payload fields:
- `appKey` (24-char hashed identity)
- `category`
- `confidence` (`high` or `low`)
- `dwellStartUtc` (ISO-8601 "O" format UTC timestamp of the current dwell slice start)

Purpose: enables `AppFeatureAggregator` to synthesize an open-dwell overlap segment for
windows where `AppDwell` has not yet fired. Preserved through buffer compaction so that
the aggregator always has a heartbeat for the current foreground app even after old events
are pruned.
```

---

### Task 3: Update `AGENT_GUIDE.md` — Fix stale services list and doc paths

**Files:**
- Modify: `docs/active/AGENT_GUIDE.md`

- [ ] **Step 1: Fix Always-on services section**

Replace the always-on services list:

Old:
```
4. Feature pipeline:
   - FeatureExtractorService (live extraction is forced off in DatasetCollection mode)
   - KeyboardCommandService
```

New:
```
4. Feature pipeline:
   - FeatureExtractorService (live extraction is forced off in DatasetCollection mode)
5. Feature upload/cleanup (all modes):
   - FeatureCsvStreamService (per-row text/csv POST to backend; no-op drain in DatasetCollection mode)
   - FeatureCleanupService
```

- [ ] **Step 2: Fix Normal mode services**

Replace Normal mode section:

Old:
```
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
```

New:
```
Normal mode only (`Agent:Mode=Normal`):
6. Status pipeline:
   - `StatusPollService`
   - `DecisionProcessorService`
```

- [ ] **Step 3: Update doc path references in Related docs section**

Change all `docs/X.md` paths to `docs/active/X.md` in section 8:
- `docs/ARCHITECTURE.md` → `docs/active/ARCHITECTURE.md`
- `docs/COLLECTORS.md` → `docs/active/COLLECTORS.md`
- `docs/EXTRACTOR.md` → `docs/active/EXTRACTOR.md`
- `docs/AGGREGATOR_SIGNAL_INVENTORY.md` → `docs/active/AGGREGATOR_SIGNAL_INVENTORY.md`

---

### Task 4: Update `ARCHITECTURE.md` — Fix reading order paths

**Files:**
- Modify: `docs/active/ARCHITECTURE.md`

- [ ] **Step 1: Fix recommended reading order file paths**

In the "Recommended reading order for contributors" section, update all `docs/X.md` paths:
- `docs/AGENT_GUIDE.md` → `docs/active/AGENT_GUIDE.md`
- `docs/COLLECTORS.md` → `docs/active/COLLECTORS.md`
- `docs/EXTRACTOR.md` → `docs/active/EXTRACTOR.md`
- `docs/AGGREGATOR_SIGNAL_INVENTORY.md` → `docs/active/AGGREGATOR_SIGNAL_INVENTORY.md`

And the per-module references:
- `src/Bootstrap/README.md` stays same (those paths are correct)

---

### Task 5: Update `DATASET_EXPORT_OUTPUTS.md` — Add missing signal types

**Files:**
- Modify: `docs/active/DATASET_EXPORT_OUTPUTS.md`

- [ ] **Step 1: Add AppFocusHeartbeat to Application usage signal types**

In the `signal_type` list under `#### Application usage`, add:
```
- `AppFocusHeartbeat`
```

- [ ] **Step 2: Add power signals section**

After the `#### Session state` signal list (after `DisplayDimmed`), add a new subsection:

```markdown
#### Power state
- `PowerSuspend`
- `PowerResume`
- `CollectionGapDetected`
```

- [ ] **Step 3: Add detailed signal reference entries**

After the `SystemResourceTick` detailed entry and before the `Heartbeat` entry, add:

```markdown
#### `AppFocusHeartbeat`
- Collector: `ApplicationUsageCollector`
- `signal_kind`: `state_sample`
- `native_cadence_sec`: `15`
- `native_aggregation_sec`: `null`
- Purpose: periodic heartbeat during sustained foreground focus; enables open-dwell synthesis in replay.
- Payload keys:
  - `appKey`
  - `category`
  - `confidence`
  - `dwellStartUtc` (ISO-8601 "O" format)

#### `PowerSuspend`
- Collector: `SessionStateCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: machine entering sleep or hibernate.
- Payload keys:
  - `reason` (`PBT_APMSUSPEND`)

#### `PowerResume`
- Collector: `SessionStateCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: machine resumed from sleep or hibernate.
- Payload keys:
  - `reason` (`PBT_APMRESUMEAUTOMATIC`)
  - `suspendUtc` (optional — ISO-8601 UTC, present if preceding `PowerSuspend` was observed)
  - `gapSec` (optional — seconds of gap, present if preceding suspend was observed)

#### `CollectionGapDetected`
- Collector: `SessionStateCollector`
- `signal_kind`: `event`
- `native_cadence_sec`: `null`
- `native_aggregation_sec`: `null`
- Purpose: marks a collection-unavailability interval following a resume. Feature windows overlapping this gap are flagged `has_collection_gap=1`.
- Payload keys:
  - `gapStartUtc`
  - `gapEndUtc`
  - `gapSec`
  - `reason` (`sleep`)
```

---

### Task 6: Update `FEATURE_SCHEMA.md` — Add AppFocusHeartbeat as source signal

**Files:**
- Modify: `docs/active/FEATURE_SCHEMA.md`

- [ ] **Step 1: Update app_window_features signals line**

Change:
```
Signals: `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate`.
```

To:
```
Signals: `AppDwell`, `ForegroundAppChanged`, `AppSwitchRate`, `AppFocusHeartbeat`.
```

- [ ] **Step 2: Update the aggregator description**

After "The aggregator builds overlap segments..." add a note:
```
If the latest `AppFocusHeartbeat` in context has no closing `AppDwell`, the aggregator synthesizes an open-dwell segment `[dwellStartUtc, window.EndUtc)` for the current foreground app and clips it to the window.
```

---

### Task 7: Add Phase 8 to development history

**Files:**
- Modify: `docs/history/2026-06-10-endpoint-signal-agent-development-history.md`

- [ ] **Step 1: Update document metadata**

Change document header:
- Total commits: 111 → ~125 (approximate through Jun 14)
- Date range end: 2026-06-10 → 2026-06-14
- Generated: 2026-06-10 → 2026-06-14

Update Phase Overview table to add Phase 8.

- [ ] **Step 2: Add Phase 8 narrative section**

Add after Phase 7 section and before "Architectural Evolution Summary":

```markdown
## Phase 8 — AppFocusHeartbeat & Replay Pipeline Fix
**2026-06-11 to 2026-06-14 · ~14 commits**

### Narrative

Phase 8 addressed a systematic data-quality gap discovered during post-collection analysis: in windows where a user had a single app open for the entire window duration without switching, `AppDwell` would not fire until the app was eventually switched away. Any feature window computed from a replay that ended before that switch would see **zero app-attribution time**, even though an app was clearly in the foreground.

The root cause was that `AppDwell` is only emitted when a dwell *ends*. A window that covers an open, still-running dwell had no signal to attribute the time to.

The fix introduced **`AppFocusHeartbeat`** — a new `StateSample` signal type emitted every `15s` by `ApplicationUsageCollector` while any foreground dwell is open. The heartbeat carries the dwell's start timestamp (`dwellStartUtc`), which allows `AppFeatureAggregator` to synthesize an open-dwell segment `[dwellStartUtc, window.EndUtc)` for the current app and clip it to the target window. This fully resolves the zero-attribution problem for live collection going forward.

For **historical data** already collected without heartbeats, `AppDwellReplayPreprocessor` was added — a preprocessing pass that reads a `raw_signals.jsonl` file, identifies every gap between a `ForegroundAppChanged` and a subsequent `AppDwell`, and injects synthetic `AppFocusHeartbeat` events at 15-second intervals within those gaps. The injected heartbeats carry `dwellStartUtc` derived from the preceding `ForegroundAppChanged` timestamp, exactly replicating what the live collector would have emitted. The file-replay path (`DatasetExportService` and the on-demand extractor) was updated to run this preprocessor before extracting features.

`FeatureVersion` was bumped from `1.2.1` to `1.2.2` to mark rows computed with the open-dwell fix.

The Data_Recovery branch, merged in this phase, included tools to recover and analyze data from a collection session where a sleep-gap recovery issue had been identified.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `24da8a6` | 2026-06-10 | fix: drain empty sleep | Sleep gap drain edge case fix |
| `c774f40` | 2026-06-10 | chore: sleep data recover | Sleep data recovery tooling |
| `b590e58` | 2026-06-11 | docs: document AppFocusHeartbeat signal and open-dwell synthesis | Full AppFocusHeartbeat implementation: signal type, emission (every 15s), aggregator synthesis, compaction preservation, tests |
| `e749337` | 2026-06-11 | feat: bump FeatureVersion to 1.2.2 | Schema version reflects open-dwell fix |
| `cc0d4f2` | 2026-06-11 | Merge Data_Recovery branch | Sleep recovery work integrated |
| `c5097a2` | 2026-06-11 | feat: AppDwellReplayPreprocessor | Synthetic heartbeat injection for historical replay |
| `ff67700` | 2026-06-11 | feat: inject synthetic AppFocusHeartbeats in file replay path | Replay pipeline uses preprocessor for retroactive fix |
| `12421af` | 2026-06-14 | chore: docs | Docs reorganized into active/decrecated/history/Thesis subdirectories |

### Design Decisions

- **Heartbeat cadence of 15s**: Short enough to provide accurate open-dwell approximation within a 30-second window slide, long enough to avoid flooding the signal stream. At 15s, the maximum attribution error for an open window ending mid-dwell is 15 seconds (half a heartbeat interval).
- **`dwellStartUtc` in heartbeat payload**: Including the dwell start time in the heartbeat allows the aggregator to reconstruct the full dwell segment `[start, windowEnd)` without needing to scan back through prior events. This is critical for correct overlap math when the compaction horizon has pruned earlier events.
- **Retroactive replay fix via preprocessor**: Rather than marking historical data as permanently contaminated, the preprocessor approach allows previously collected raw signal files to be re-extracted with the correct app attribution, making historical dataset rows directly comparable to future live-collected rows.
- **FeatureVersion 1.2.2**: The minor version bump (1.2.1 → 1.2.2) signals a non-breaking improvement — the schema shape is unchanged, but rows at 1.2.2 have more accurate app-feature values than 1.2.1 rows from the same collection period.
- **Docs reorganization**: The `docs/` root was reorganized into `docs/active/` (living reference docs), `docs/decrecated/` (completed plans and obsolete designs), `docs/history/` (chronological development history), and `docs/Thesis/` (academic thesis documents). This separates always-current reference material from historical artifacts.
```

- [ ] **Step 3: Update Architectural Evolution Summary table**

Add Phase 8 column to the existing table.

- [ ] **Step 4: Update Feature Schema Version History table**

Add row:
```
| 1.2.2 | Phase 8 (Jun 11, 2026) | Open-dwell synthesis via `AppFocusHeartbeat`; retroactive replay fix |
```

- [ ] **Step 5: Update Signal Taxonomy table**

Add `AppFocusHeartbeat` to the Application domain row.

- [ ] **Step 6: Update Document Metadata**

Change:
- `Generated: 2026-06-10` → `Generated: 2026-06-14`
- `Total commits surveyed: 111` → `Total commits surveyed: ~125`
- `Date range: 2026-01-13 to 2026-06-10` → `2026-01-13 to 2026-06-14`

---

### Task 8: Commit everything

- [ ] **Step 1: Stage all changes**

```bash
git add docs/active/ docs/history/ docs/decrecated/ docs/Thesis/ docs/superpowers/
git add -u docs/
```

- [ ] **Step 2: Commit**

```bash
git commit -m "docs: reorganize into active/decrecated/history subdirs; update for v1.2.2 and AppFocusHeartbeat

- Move all docs/* into docs/active/, docs/decrecated/, docs/history/, docs/Thesis/
- EXTRACTOR.md: FeatureVersion 1.2.1 -> 1.2.2
- COLLECTORS.md: document AppFocusHeartbeat signal (15s open-dwell heartbeat)
- AGENT_GUIDE.md: remove stale BatchProducerService/BatchSendService/FeatureUploadService/KeyboardCommandService; add FeatureCsvStreamService; fix doc paths
- ARCHITECTURE.md: fix reading order doc paths to docs/active/
- DATASET_EXPORT_OUTPUTS.md: add AppFocusHeartbeat, PowerSuspend, PowerResume, CollectionGapDetected
- FEATURE_SCHEMA.md: add AppFocusHeartbeat as source signal for open-dwell synthesis
- history: append Phase 8 (AppFocusHeartbeat, v1.2.2, replay preprocessor, docs reorg)

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```
