# EndpointSignalAgent — Development History Replay

> **Document purpose:** This is a professional, chronological reconstruction of the full development history of the `EndpointSignalAgent` project, derived from a complete sweep of all 111 commits on the `deployment_prep` branch. It is intended for academic analysis and should be read as a narrative of design decisions, architectural pivots, and incremental capability growth across a six-month development period (January–June 2026).

---

## Project Summary

**EndpointSignalAgent** is a Windows tray application written in C# (.NET) that passively collects behavioral endpoint signals from a user's workstation — foreground application usage, keyboard activity, network state, session lifecycle events, screen saver state, and system resource consumption. These raw signals are continuously extracted into a fixed-schema feature vector (a "feature window") and persisted locally. The system supports two operating modes: a **Normal mode** that can optionally forward features to a backend service, and a **DatasetCollection mode** that wraps collection sessions with structured annotations for supervised-learning dataset construction.

**Branch name:** `deployment_prep`
**Total commits:** ~125 (111 through Jun 10; ~14 added in Phase 8)
**Date range:** 2026-01-13 to 2026-06-14
**Primary language:** C# (.NET)
**Platform target:** Windows (tray app, P/Invoke, Win32 APIs)

---

## Phase Overview

| Phase | Date Range | Theme | Key Commits |
|---|---|---|---|
| 1 | Jan 13 – Jan 26 | Foundation & Core Architecture | 18 commits |
| 2 | Feb 8 – Feb 27 | Feature Extraction & Aggregation | 8 commits |
| 3 | Mar 2 – Mar 8 | Session State, Network Refactor & Tray App | 12 commits |
| 4 | Apr 10 – Apr 24 | System Resource Collector | 14 commits |
| 5 | Apr 23 – May 17 | Data Pipeline Maturation & Docs | 12 commits |
| 6 | Jun 1 – Jun 8 | Stabilization & Production Hardening | 11 commits |
| 7 | Jun 10 | Sleep/Lock/Power-Gap Feature | 13 commits |
| 8 | Jun 11 – Jun 14 | AppFocusHeartbeat & Replay Pipeline Fix | ~14 commits |

---

## Phase 1 — Foundation & Core Architecture
**2026-01-13 to 2026-01-26 · 18 commits**

### Narrative

The project began with a single "Initial commit" on January 13, 2026 and immediately entered a rapid prototyping phase. The first two weeks were characterized by exploratory, work-in-progress commits (`wip`) establishing the architectural skeleton: a channel-based signal bus, a spool writer, and the first behavioral collectors.

The central design decision in this phase was to use **in-process channels** (the `System.Threading.Channels` API) as the backbone for asynchronous signal flow. Each collector pushes typed signal events into a shared channel; a downstream reader drains and persists them. This decoupled the collection rate from the persistence rate, avoiding the write-collision issues that surfaced and were fixed in this phase.

The first behavioral sensors wired up were:
- **Foreground application switching** (`cef8214`) — tracking which application has input focus
- **Screen saver state** (`7c3a787`) — detecting display idle state
- **Network context** (`388b378`, `fc8a1aa`) — tracking local network changes, VPN state, and WiFi link events

The spool file (`signals.jsonl`) was the primary persistence target in this phase: a newline-delimited JSON log that accumulated all raw signal events for later extraction.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `ad1bbb4` | 2026-01-13 | Initial commit | Project genesis |
| `7051a11` | 2026-01-14 | chore: runable demo | First working end-to-end run |
| `d4ad424` | 2026-01-14 | Stop tracking build artifacts | `.gitignore` hygiene establishing repo cleanliness |
| `171e2b8` | 2026-01-14 | chore: test mock | First test scaffolding |
| `d4a38f1` | 2026-01-14 | wip: 2 loops | Exploration of dual consumer loop model |
| `90b4282` | 2026-01-18 | wip: sender and status poll | Early backend communication prototype |
| `1eb57ff` | 2026-01-18 | feat: refactor | First architectural refactor pass |
| `a26d6fb` | 2026-01-18 | wip: spool collector | Spool channel reader take shape |
| `c1d89ca` | 2026-01-19 | feat: backend communicate | Initial backend send path wired |
| `cef8214` | 2026-01-20 | feat: foreapp switching | Foreground application signal collector |
| `8ced309` | 2026-01-20 | chore: ignore spool | Exclude runtime artifacts from version control |
| `0a8179c` | 2026-01-20 | wip | Ongoing channel plumbing |
| `7c3a787` | 2026-01-20 | feat: screen saver | Screen saver state signal |
| `388b378` | 2026-01-21 | feat: base network collector | LocalNetworkChanged, VpnStateChanged, WifiLinkChanged signals |
| `fc8a1aa` | 2026-01-21 | feat: network update | Expanded network context tracking |
| `07ab3ef` | 2026-01-22 | fix: collector write collision | Fixed concurrent write hazard in spool writer |
| `68662d5` | 2026-01-26 | refactor: collector write to channel | All collectors now push through the channel bus (key architectural normalization) |

### Design Decisions

- **Channel bus architecture**: Rather than each collector writing directly to disk, a single channel mediates all signal flow. This choice would remain central to the system throughout its entire history.
- **JSONL spool**: Raw signals persisted as newline-delimited JSON for simplicity and append-friendliness. This was later supplemented (then partially replaced) by SQLite and CSV formats.
- **Tray app deferred**: In Phase 1 the application ran as a console/worker service. The user-visible tray UI was deferred to Phase 3.

---

## Phase 2 — Feature Extraction & Aggregation
**2026-02-08 to 2026-02-27 · 8 commits**

### Narrative

After the signal collection plumbing was in place, Phase 2 introduced the **feature extraction layer** — the transformation of raw signal events into time-windowed numerical feature vectors. This is the core analytical primitive of the system: raw events are aggregated over a fixed window (e.g., 60 seconds) into a structured row describing the behavioral state of the endpoint.

Work in this phase was exploratory and iterative. The `wip` commits reflect active investigation of the multi-aggregate table design, where multiple signal types needed to contribute columns to a single flat feature row. A write-ordering bug in the aggregation table was identified and fixed.

The phase also introduced **keyboard signal capture** backed by a SQLite database (`2969dcc`), marking the system's first use of structured local persistence beyond the append-only JSONL spool.

The **feature extract toggle** (`8aed4d5`) showed early awareness that live extraction should be independently controllable from signal collection — a design choice that would later formalize into `FeatureExtractor:EnableLiveExtraction` in configuration.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `8c9a700` | 2026-02-08 | chore: setting up | Extraction pipeline scaffolding |
| `3c691f4` | 2026-02-08 | refactor: clean up folder | Module boundary formalization; channel reader split for spool vs extractor |
| `2969dcc` | 2026-02-09 | feat: keyboard control database | SQLite-backed keyboard signal persistence |
| `5eafaad` | 2026-02-10 | wip: multi aggregate | Multi-signal aggregation exploration |
| `d061bc8` | 2026-02-10 | fix: aggregate table | Write ordering bug in aggregation table fixed |
| `5ad81b5` | 2026-02-10 | wip fix state start | Session state initialization edge case |
| `4108da1` | 2026-02-23 | chore: toggle backend | Backend toggle control added |
| `5932834` | 2026-02-24 | fix: add idle to total duration | Idle time correctly accounted in duration aggregation |
| `8aed4d5` | 2026-02-24 | feat: feature extract toggle | Live extraction can be independently enabled/disabled |
| `d29236e` | 2026-02-27 | wip | Continued aggregation refinement |

### Design Decisions

- **Separation of spool channel from extractor channel**: The `3c691f4` refactor split the single reader into two independent consumers — one for the JSONL spool, one for the feature extractor. This decoupling allowed each pipeline to evolve independently.
- **SQLite for keyboard data**: The choice to use SQLite (rather than JSONL) for keyboard signals reflected the need for indexed, queryable behavioral history. This SQLite database (`features.db`) would become the primary feature persistence store.
- **Feature extract toggle**: Early recognition that data collection and feature extraction have different uptime requirements. This would later manifest as separate `Mode` configurations.

---

## Phase 3 — Session State, Network Refactor & Tray App
**2026-03-02 to 2026-03-08 · 12 commits**

### Narrative

Phase 3 was the most structurally significant phase of the project. Three major deliverables landed in rapid succession: a full **session state refactor**, a **network collector overhaul**, and the introduction of the **Windows tray application** UI — the latter delivered across three consecutive pull requests (PR #1, #2, #3).

The session state refactor (`927e9ae`, `b15cbe6`) consolidated how the system tracked user session lifecycle (active, idle, locked, away). This was critical for feature accuracy: duration columns in the feature vector needed to partition time correctly between active and idle states.

Network collector cleanup (`2507c11`) removed duplicated WiFi/VPN signals that were being emitted twice due to overlapping collector registrations (`d39ce03`).

The tray app transformation was delivered via three iterative pull requests, each rebasing the `StatExtration` (sic) branch. This approach — consecutive PRs building on each other — suggests the tray UI was developed alongside active data collection. PR #1 established the tray scaffolding; PR #2 added collection control (pause/resume) and the host bootstrap; PR #3 fixed the startup working directory so runtime artifacts (`spool/`, CSV exports) wrote relative to the executable instead of `C:\Windows\System32`.

The CSV export action (`0d7f204`) — a tray menu item that dumps all extracted features to a flat file — was introduced here, providing the first human-readable output of the feature extraction pipeline.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `3bbcc73` | 2026-03-02 | wip | Session state work begins |
| `fa8a286` | 2026-03-02 | chore: update doc | Documentation catch-up |
| `2507c11` | 2026-03-02 | refactor: update network collectors | Network collector consolidation |
| `9a10bd3` | 2026-03-03 | fix: TypeLoadException | DI resolution error fixed (likely from module restructure) |
| `10f51e6` | 2026-03-04 | refactor: app usage and doc | App usage collector refactor |
| `927e9ae` | 2026-03-05 | refactor: sess state | Session state data model refactor |
| `b15cbe6` | 2026-03-05 | refactor: session state | Session state finalization |
| `d39ce03` | 2026-03-05 | fix: get rid dupl wifi vpn | Duplicate WiFi/VPN signal emission removed |
| `8bec2df` | 2026-03-07 | Merge PR #1 | Tray app, host bootstrap, collection control, startup scripts |
| `e7be05b` | 2026-03-07 | Merge PR #2 | Tray UI enhancements (collection control) |
| `7c3f10f` | 2026-03-07 | Add one-click startup setup/removal scripts | `install-logon-startup.cmd` / `remove-logon-startup.cmd` for end-user distribution |
| `8bc8ce7` | 2026-03-08 | Merge PR #3 | Startup working directory fix (artifacts beside executable) |
| `dcc5ee3` | 2026-03-08 | Fix startup working directory | Spool/CSV paths resolve to app folder, not system directory |

### Design Decisions

- **Tray app as the primary UI surface**: The decision to deliver a tray icon (rather than, say, a settings window or CLI) reflected the passive, always-on nature of the agent. The application needed to run unobtrusively at logon with minimal user interaction.
- **Logon startup scripts**: By shipping `install-logon-startup.cmd` alongside the binary, the team anticipated distribution to study participants who would not have repository access. This was a pragmatic product decision — making participant onboarding a double-click operation.
- **Iterative PR strategy**: Three consecutive PRs on the same tray feature, each rebasing from `StatExtration`, indicates that the tray work was being delivered incrementally while the main branch remained in active development.

---

## Phase 4 — System Resource Collector
**2026-04-10 to 2026-04-24 · 14 commits**

### Narrative

Phase 4 added the **system resource dimension** to the feature vector: CPU utilization, memory pressure, and GPU load. This work was technically the most complex to date due to the initial choice of WMI (Windows Management Instrumentation) for resource sampling — a choice that was subsequently reversed.

The `SystemResourceCollector` was first implemented using WMI (`814d8a5`), which provided a high-level .NET API for querying hardware counters. Two sub-features were then added in separate branches: network-related resource metrics (PR #5) and a WMI removal refactor (PR #6).

The WMI removal (`0379ab5`, PR #6) replaced WMI queries with direct **native Win32 API calls** (P/Invoke). This was a deliberate decision: WMI has high initialization overhead, produces unreliable readings in some Windows configurations, and requires elevated permissions in certain environments. The Win32 path is faster, more reliable, and requires no special permissions. The GPU sub-collector was disabled (due to availability issues) and a fix for GPU-disabled feature handling was applied (`686ac37`).

The phase also introduced a temporary dataset-collection setting (`f201d57`) — a targeted configuration change to force specific collection behavior during an active data gathering period.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `1777f07` | 2026-04-10 | chore: git ignore | Build artifact exclusions |
| `814d8a5` | 2026-04-11 | Add SystemResourceCollector with CPU, memory, GPU signals | Initial WMI-based implementation |
| `7bf9bf1` | 2026-04-11 | chore: enable console debug | Debug visibility during resource collector testing |
| `88b5b6d` | 2026-04-12 | wip: network resource | Network resource metrics exploration |
| `ac8bd3f` | 2026-04-12 | Integrate system resource features into extractor pipeline | Feature schema extended with resource columns |
| `af9db04` | 2026-04-12 | merge | Branch merge |
| `f1f744e` | 2026-04-12 | Merge PR #5 | System resource feature extraction + extractor pipeline integration; FeatureVersion bumped to 1.2 |
| `0379ab5` | 2026-04-12 | Refactor system resource sampling to native Win32 APIs | WMI removed; P/Invoke replaces it |
| `46e8dca` | 2026-04-12 | wip | Win32 refactor in progress |
| `2d63191` | 2026-04-13 | chore: update git ignore | |
| `686ac37` | 2026-04-13 | Fix system resource availability and GPU-disabled feature handling | GPU column emits zero when hardware unavailable |
| `14331e7` | 2026-04-13 | Merge PR #6 | Win32-native SystemResourceCollector merged; WMI fully removed |
| `554cd9b` | 2026-04-15 | wip: make work | Post-merge stabilization |
| `2878ec0` | 2026-04-15 | Update SystemResourceCollector.md | Documentation updated post-refactor |
| `2e10427` | 2026-04-15 | wip | |
| `2b61da5` | 2026-04-16 | wip make work | |
| `f201d57` | 2026-06-01 | Temp: dataset setting | Temporary configuration for active data collection run (flagged for revert) |

### Design Decisions

- **WMI → Win32 pivot**: The rapid reversal of WMI (implemented, then removed within two days) reflects a pragmatic reaction to real-world testing. WMI's initialization cost and reliability issues on end-user machines made it unsuitable for a background agent with a low resource footprint requirement.
- **GPU disabled by default**: Rather than blocking on GPU availability, the system emits a zero-filled GPU column when hardware is unavailable. This kept the feature schema stable regardless of machine configuration.
- **Feature version bump to 1.2**: The addition of system resource columns required a schema version bump, establishing the practice of tracking breaking schema changes in `FeatureVersion`.

---

## Phase 5 — Data Pipeline Maturation & Documentation
**2026-04-23 to 2026-05-17 · 12 commits**

### Narrative

With all major signal collectors in place, Phase 5 focused on **data pipeline maturation**: improving the reliability of the write path, establishing comprehensive documentation, and hardening the raw signal output format.

The `raw data rework` (`6be994d`) and `system resource collector rework` (`9ed08f6`) indicate that the initial signal formats were revised — likely to normalize column naming and ensure consistent downstream parsing. The `wip: write async` and `wip: catch` commits (`b2d2620`, `1c08e37`) reflect investigation into asynchronous write patterns to prevent blocking the UI thread during spool flushes.

The documentation sprint (`8a907f0`) was notable: a single commit added per-module `README.md` files to every source directory (`src/Bootstrap/`, `src/Tray/`, `src/Shared/`, `src/SignalCollection/`, `src/FeatureExtraction/`, `src/DatasetCollection/`) and updated the top-level `ARCHITECTURE.md` and `EXTRACTOR.md`. This indicates a deliberate transition from exploratory development toward a state where the project could be handed off or contributed to by others.

The `chore: from raw to db to csv` commit (`42036c5`) formalized the three-stage data flow: raw signal events → SQLite feature database → CSV export.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `b95c992` | 2026-04-18 | chore: data output doc | Data output format documentation |
| `6be994d` | 2026-04-18 | feat: raw data rework | Raw signal format revision |
| `719a203` | 2026-04-18 | chore: docs update | Documentation synchronization |
| `9ed08f6` | 2026-04-19 | feat: system resource collector rework | Collector signal format revision |
| `de10d61` | 2026-04-19 | wip | |
| `f66664a` | 2026-04-19 | doc | |
| `2e7488c` | 2026-04-20 | chore: auto collection session | Automatic session lifecycle management |
| `cd44523` | 2026-04-20 | docs: export doc | Export documentation |
| `49fb702` | 2026-04-23 | feat: tray update | Tray menu additions |
| `1c08e37` | 2026-04-23 | wip: catch | Exception handling in write path |
| `b2d2620` | 2026-04-23 | wip: write async | Async write investigation |
| `8a907f0` | 2026-04-23 | docs | Per-module README sweep; full EXTRACTOR.md update to 17-field FeatureRow |
| `4057256` | 2026-04-24 | fix: 1000 write overflow | Write buffer overflow at high signal volume fixed |
| `7e9335b` | 2026-05-03 | chore: tray app icon | Custom tray icon asset |
| `42036c5` | 2026-05-03 | chore: from raw to db to csv | Three-stage pipeline formalized; `.gitignore` updated |
| `b8e2cda` | 2026-05-17 | ' | Minor working commit |
| `ea6906c` | 2026-05-17 | ' | Minor working commit |
| `9b725b8` | 2026-05-17 | ' | Minor working commit |

### Design Decisions

- **Three-stage pipeline (raw → db → csv)**: The formal articulation of this pipeline — raw JSONL spool → SQLite feature database → CSV export — established a clear data lineage. Each stage could be independently inspected and the CSV provided a universal downstream format for analysis tools.
- **Per-module READMEs**: The documentation sprint indicated the project was transitioning toward participant-study deployment, where others (or AI coding agents) would need to onboard quickly. Each module got a focused README describing its responsibility.
- **Write overflow fix**: The 1,000-item write overflow (`4057256`) indicated that high-frequency signal scenarios could overwhelm the initial write buffer. The fix established a hard backpressure limit on the channel.

---

## Phase 6 — Stabilization & Production Hardening
**2026-06-01 to 2026-06-08 · 11 commits**

### Narrative

Phase 6 was a deliberate stabilization sprint, driven by the recognition that the system was approaching deployment for actual data collection. The work focused on four problems: **unbounded storage growth**, **upload reliability**, **production configuration cleanliness**, and **idle security**.

The storage problem was addressed on two fronts. The JSONL spool (`signals.jsonl`) could grow without bound on a long-running machine; a **50 MB rotation mechanism** (`dab2c3e`) was added that renames the current file to `.bak` when the threshold is exceeded. Concurrently, the SQLite feature database (`features.db`) was put into **WAL (Write-Ahead Logging) mode** (`9f64689`) for improved concurrent read/write performance, and a **hard cap of 500 unsent rows** was enforced in `FeatureCleanupService` to prevent indefinite accumulation of undelivered features.

The upload path was replaced. The original JSON batch upload (`BatchSend`, `FeatureUpload`) was removed entirely (`3ea0cd9`) in favor of a **per-row CSV streaming approach** (`07d3e89`, `3b440ad`): a fixed-schema 104-column serializer and a streaming HTTP POST service that sends each feature row as a `text/csv` line. This eliminated the batch size problem and reduced memory pressure on upload.

A `DeviceGuardService` (`5f2796c`) was added — an optional, disabled-by-default service that can trigger the Windows lock screen or sleep state after a configurable idle period. This was a participant safety feature: preventing the machine from remaining in an unlocked state during a data collection session if the participant steps away.

The dead code sweep (`8f65ee3`) removed `BatchProducer`, `BatchSend`, `WifiSsidHash`, `CurrentFeatureVersion`, `enableConsoleDebug`, and orphaned configuration keys. This cleanup reduced the cognitive surface area of the codebase significantly.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `3536972` | 2026-06-01 | plan | Planning document committed |
| `3ea0cd9` | 2026-06-08 | refactor: remove legacy test scaffolding | HeartbeatProvider, KeyboardCommand, BatchSend, FeatureUpload removed |
| `3b440ad` | 2026-06-08 | feat: add FeatureCsvRowSerializer — fixed-schema 104-column CSV helper | CSV serialization foundation |
| `07d3e89` | 2026-06-08 | feat: add FeatureCsvStreamService — per-row text/csv POST | Per-row streaming upload replaces JSON batch |
| `9f64689` | 2026-06-08 | feat: FeatureStore WAL mode + hard unsent-row cap (500) | SQLite reliability + storage cap |
| `dab2c3e` | 2026-06-08 | feat: spool file rotation at 50 MB | Unbounded spool growth prevention |
| `5f2796c` | 2026-06-08 | feat: DeviceGuardService — idle-triggered lock/sleep | Participant session security guard |
| `7d0292c` | 2026-06-08 | chore: expose FeatureRowCsvPath and DeviceGuard config | Configuration surface for new services |
| `7dbc3e3` | 2026-06-08 | docs: update ARCHITECTURE and EXTRACTOR | Documentation reflects stabilization changes |
| `8f65ee3` | 2026-06-08 | refactor: dead code sweep | Legacy code removed; codebase reduced to active paths only |

### Design Decisions

- **Per-row CSV streaming vs. JSON batching**: The switch from batch JSON to per-row CSV streaming eliminated the need to buffer features in memory before upload, reduced upload failure blast radius (one failed row vs. a failed batch), and aligned the wire format with downstream analytical tooling (CSV is universally readable).
- **50 MB spool rotation**: A rotation-to-.bak model was chosen over a rolling window or deletion policy because it preserved the most recent prior data for debugging without requiring a ring buffer implementation.
- **WAL mode for SQLite**: WAL mode allows concurrent readers without blocking writers, which is important because the extractor writes rows while the CSV export (tray action) reads them simultaneously.
- **DeviceGuard disabled by default**: The service exists in the codebase and is configurable, but defaults to off. This respects participant autonomy while providing study administrators a configuration knob.

---

## Phase 7 — Sleep, Lock & Power-Gap Feature
**2026-06-10 · 13 commits**

### Narrative

Phase 7, delivered entirely on June 10, 2026, introduced the most sophisticated signal domain in the project: **power state awareness**. This feature addressed a fundamental data quality problem that had likely been present since Phase 1: when a machine suspends to sleep and resumes, the feature extractor continues as if no time had passed, creating a spurious "feature window" that spans a sleep event. This contaminates the feature vector with meaningless duration columns covering the sleep period.

The work proceeded in a clear, well-structured sequence: type definitions first, then signal emission, then collection control, then feature stamping, then schema serialization, then documentation.

**New signal types** (`c3704c2`): `PowerSuspend`, `PowerResume`, and `CollectionGapDetected` were added to the signal taxonomy, along with `IsSessionLocked` to `ICollectionControl`.

**Signal emission** (`e9dee8c`): `SessionEventWindowListener` was extended to listen to Windows power management messages (`PBT_APMSUSPEND` / `PBT_APMRESUMEAUTOMATIC`) via the existing Windows message pump. The choice of `PBT_APMRESUMEAUTOMATIC` (rather than `PBT_APMRESUMESUSPEND`, which is user-triggered) was documented explicitly: it fires for both automatic and user-triggered resumes, ensuring gap detection works regardless of the resume cause.

**Collection throttling** (`b55e85c`, `49e6e5d`, `b0c4288`): When the session is locked, all polling collectors (SystemResource, NetworkContext, ApplicationUsage) slow from their normal 2-second cadence to a **30-second cadence**. This mirrors the pre-existing idle loop pattern in `SessionStateCollector` and significantly reduces CPU/IO overhead when the machine is locked.

**Feature stamping** (`fb27fac`): Two new quality columns were added to the feature vector — `has_collection_gap` (boolean: did any PowerSuspend event occur during this window?) and `in_warm_up` (boolean: is this window within the post-resume warm-up period, during which sensor readings may be unreliable?). The `FeatureExtractorService` now tracks gap intervals and post-resume warm-up duration.

**Schema fix** (`7414cec`, `24b4dc2`): The two new columns were added to `FeatureSchema.AllColumns` (fixing the serializer) and the on-demand extraction path was updated to default both fields to `0`.

**Application categorization expansion** (`89cb8c8`, `ed46ed4`): A separate but same-day feature expanded `CategorizeFromFileInfo` and `InferFromName` to cover all 13 application categories, using a frozen dictionary for known apps and a concurrent dictionary for inferred results, with a zero-allocation `StripArchSuffix` span helper.

**Documentation** (`2c64b18`, `5c37cbd`, `32b1b19`): The full documentation suite was updated: `SIGNAL_CATALOG`, `COLLECTORS`, `EXTRACTOR`, `ARCHITECTURE`, `AGGREGATOR_SIGNAL_INVENTORY`, and the per-collector READMEs. The `FeatureVersion` was bumped to `1.2.1`.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `c3704c2` | 2026-06-10 | feat: add PowerSuspend/PowerResume/CollectionGapDetected signal types | Signal taxonomy extended; IsSessionLocked added to ICollectionControl |
| `b55e85c` | 2026-06-10 | feat: wire SessionStateCollector lock/unlock into CollectionControl.SetSessionLocked | Lock state propagated to collection control |
| `49e6e5d` | 2026-06-10 | feat: slow polling collectors to 30s cadence when session is locked | Adaptive polling cadence under lock |
| `b0c4288` | 2026-06-10 | refactor: expose IsSessionLocked on SignalCollectorBase | IsSessionLocked hoisted to base class; redundant fields removed |
| `e9dee8c` | 2026-06-10 | feat: emit PowerSuspend/PowerResume/CollectionGapDetected via PBT_APMSUSPEND/PBT_APMRESUMEAUTOMATIC | Windows power messages captured in SessionEventWindowListener |
| `e2cfc4e` | 2026-06-10 | refactor: collapse OnPowerResume into single HasValue branch | Redundant gapSec computation eliminated |
| `fb27fac` | 2026-06-10 | feat: stamp has_collection_gap and in_warm_up onto feature windows | Quality columns computed from PowerResume intervals |
| `7414cec` | 2026-06-10 | fix: add has_collection_gap and in_warm_up to FeatureSchema.AllColumns | Serializer fix — new columns now appear in CSV output |
| `24b4dc2` | 2026-06-10 | fix: default has_collection_gap/in_warm_up to 0 on on-demand path | On-demand extraction path patched; FeatureVersion bumped |
| `ed46ed4` | 2026-06-10 | chore: more app categorization | Known-app map additions |
| `89cb8c8` | 2026-06-10 | feat: expand CategorizeFromFileInfo to cover all 13 categories | FrozenDictionary for known apps; zero-allocation StripArchSuffix |
| `2c64b18` | 2026-06-10 | docs: note suspend-without-resume gap loss limitation | Design limitation documented |
| `5c37cbd` | 2026-06-10 | chore: bump FeatureVersion to 1.2.1 | Schema version reflects new quality columns |
| `32b1b19` | 2026-06-10 | docs: update all docs for sleep/lock/power-gap feature | Full documentation sweep |

### Design Decisions

- **`PBT_APMRESUMEAUTOMATIC` choice**: This Windows message fires for all resume types (user-triggered and automatic). The alternative (`PBT_APMRESUMESUSPEND`, user-triggered only) would miss timer-based or network-triggered wakes. The choice was explicitly documented in `COLLECTORS.md`.
- **Suspend-without-resume gap loss limitation**: A documented limitation: if the machine suspends and does not send `PBT_APMRESUMEAUTOMATIC` (e.g., a forced shutdown), the gap is not recorded. This is noted as an acceptable limitation for a study context where graceful suspend/resume is the norm.
- **Quality columns rather than row removal**: Rather than dropping feature rows that span a sleep event, the system stamps them with `has_collection_gap=1` and `in_warm_up=1`. Downstream analysis can then filter or weight these rows appropriately, preserving all data while flagging quality.
- **30-second locked cadence**: The adaptive polling cadence under session lock mirrors the pre-existing idle pattern in `SessionStateCollector`, maintaining internal consistency. At 30s, the system remains responsive to state changes while consuming minimal resources.
- **FrozenDictionary for known-app map**: Using `FrozenDictionary` for the static known-app lookup table provides O(1) average lookup with minimal allocation overhead — appropriate for a path called on every foreground app event.

---

## Phase 8 — AppFocusHeartbeat & Replay Pipeline Fix
**2026-06-11 to 2026-06-14 · ~14 commits**

### Narrative

Phase 8 addressed a systematic data-quality gap discovered during post-collection analysis: in windows where a user had a single app open for the entire window duration without switching, `AppDwell` would not fire until the app was eventually switched away. Any feature window computed from a replay that ended before that switch would see **zero app-attribution time**, even though an app was clearly in the foreground the entire time.

The root cause was architectural: `AppDwell` is only emitted when a dwell *ends*. A window that covers an open, still-running dwell had no signal to attribute time to.

The fix introduced **`AppFocusHeartbeat`** — a new `StateSample` signal type emitted every `15s` by `ApplicationUsageCollector` while any foreground dwell is open. The heartbeat carries `dwellStartUtc`, allowing `AppFeatureAggregator` to synthesize an open-dwell segment `[dwellStartUtc, window.EndUtc)` for the current app and clip it to the target window. This fully resolves the zero-attribution problem for live collection going forward.

For **historical data** already collected without heartbeats, `AppDwellReplayPreprocessor` was added — a preprocessing pass that reads `raw_signals.jsonl`, identifies every gap between a `ForegroundAppChanged` and a subsequent `AppDwell`, and injects synthetic `AppFocusHeartbeat` events at 15-second intervals within those gaps. The file-replay path was updated to run this preprocessor before feature extraction.

`FeatureVersion` was bumped from `1.2.1` to `1.2.2` to mark rows computed with the open-dwell fix.

The `docs/` structure was reorganized in this phase: reference docs moved to `docs/active/`, completed plans to `docs/decrecated/`, history documents to `docs/history/`, and academic content to `docs/Thesis/`.

### Key Commits

| Commit | Date | Message | Significance |
|---|---|---|---|
| `24da8a6` | 2026-06-10 | fix: drain empty sleep | Sleep gap drain edge case fix |
| `c774f40` | 2026-06-10 | chore: sleep data recover | Sleep data recovery tooling |
| `b590e58` | 2026-06-11 | docs: document AppFocusHeartbeat signal and open-dwell synthesis | Full AppFocusHeartbeat implementation: signal type, emission every 15s, aggregator synthesis, compaction preservation, tests |
| `e749337` | 2026-06-11 | feat: bump FeatureVersion to 1.2.2 | Schema version reflects open-dwell fix |
| `cc0d4f2` | 2026-06-11 | Merge Data_Recovery branch | Sleep recovery work integrated |
| `c5097a2` | 2026-06-11 | feat: AppDwellReplayPreprocessor | Synthetic heartbeat injection for historical replay |
| `ff67700` | 2026-06-11 | feat: inject synthetic AppFocusHeartbeats in file replay path | Replay pipeline uses preprocessor retroactively |
| `12421af` | 2026-06-14 | chore: docs | Docs reorganized into active/decrecated/history/Thesis subdirectories |

### Design Decisions

- **Heartbeat cadence of 15s**: Short enough for accurate open-dwell attribution within a 30s window slide. Maximum attribution error is 15s (half a heartbeat interval).
- **`dwellStartUtc` in payload**: Carrying the dwell start time in the heartbeat allows the aggregator to reconstruct the full segment `[start, windowEnd)` without scanning back through pruned history — critical when buffer compaction has removed earlier events.
- **Retroactive replay fix via preprocessor**: Rather than treating historical data as permanently contaminated, the preprocessor allows previously collected `raw_signals.jsonl` files to be re-extracted with correct app attribution, making historical rows comparable to future live rows.
- **FeatureVersion 1.2.2**: Minor bump (1.2.1 → 1.2.2) signals a non-breaking improvement — the schema shape is unchanged, but rows at 1.2.2 have more accurate app-feature values.
- **Docs reorganization**: Separates always-current reference material (`docs/active/`) from historical artifacts (`docs/decrecated/`, `docs/history/`) and thesis content (`docs/Thesis/`).

---

## Architectural Evolution Summary

The following table traces how each major component of the system evolved across phases:

| Component | Phase 1 | Phase 2 | Phase 3 | Phase 4 | Phase 5 | Phase 6 | Phase 7 | Phase 8 |
|---|---|---|---|---|---|---|---|---|
| Signal bus | Channel per collector | Split spool/extractor channels | — | — | Write overflow fixed | — | Power signals added | AppFocusHeartbeat added |
| Persistence | JSONL spool | SQLite (keyboard) | — | — | 3-stage pipeline formalized | WAL mode; 50MB rotation | — | — |
| Upload | Backend send prototype | Toggle | — | — | — | CSV streaming (per-row) | — | — |
| UI | Console/worker | — | Tray app (PR #1-3) | — | Tray icon asset | DeviceGuard service | — | — |
| Session state | — | — | Refactored | — | Auto session lifecycle | — | Lock/unlock propagated | — |
| Feature schema | Flat row prototype | Multi-aggregate; v1.0 | — | +Resource cols; v1.2 | 17-field FeatureRow | 104-col CSV schema | +Quality cols; v1.2.1 | Open-dwell synthesis; v1.2.2 |
| Collectors | ForeApp, ScreenSaver, Network | Keyboard | SessionState refactored | SystemResource (Win32) | Reworked | — | Adaptive lock cadence; Power signals | AppFocusHeartbeat emission (15s) |
| App categorization | — | — | — | — | — | — | 13 categories; FrozenDictionary | — |
| Replay pipeline | — | — | — | — | — | — | — | AppDwellReplayPreprocessor; synthetic heartbeat injection |
| Documentation | — | — | — | SystemResourceCollector.md | Per-module READMEs; full sweep | ARCHITECTURE + EXTRACTOR updated | Full docs sweep | docs/ restructured into active/decrecated/history/Thesis |

---

## Feature Schema Version History

| Version | Introduced | Change |
|---|---|---|
| 1.0 | Phase 2 (Feb 2026) | Initial multi-aggregate feature row |
| 1.2 | Phase 4 (Apr 12, 2026) | +SystemResource columns (CPU, memory, GPU); PR #5 |
| 1.2.1 | Phase 7 (Jun 10, 2026) | +`has_collection_gap`, `in_warm_up` quality columns |
| 1.2.2 | Phase 8 (Jun 11, 2026) | Open-dwell synthesis via `AppFocusHeartbeat`; retroactive replay fix via `AppDwellReplayPreprocessor` |

---

## Signal Taxonomy as of 2026-06-14

| Domain | Signal Types |
|---|---|
| Application | ForegroundAppChanged, AppDwell, AppSwitchRate, **AppFocusHeartbeat** *(added Phase 8)* |
| Screen | ScreenSaverStarted, ScreenSaverStopped |
| Network | LocalNetworkChanged, VpnStateChanged, WifiLinkChanged |
| Session | SessionLocked, SessionUnlocked, SessionIdle, SessionActive |
| Power | **PowerSuspend**, **PowerResume**, **CollectionGapDetected** *(added Phase 7)* |
| System Resources | SystemResourceTick (CPU, memory, GPU) |
| Keyboard | KeyboardActivity |

---

## Recurring Patterns

Several recurring patterns are observable across the commit history:

1. **Build-measure-refactor cycles**: Multiple collectors (SystemResource, NetworkContext, SessionState) were built, tested with real data, found wanting, and refactored. The WMI → Win32 refactor is the clearest example of this loop.

2. **WIP → fix → chore → feat progression**: Extended features often appeared first as `wip` commits, followed by `fix` commits addressing issues found in initial testing, then `chore` commits for configuration/docs, and finally a clean `feat` commit summarizing the capability.

3. **Documentation as deployment gate**: The two largest documentation sprints (Phase 5 in April, Phase 7 in June) both immediately preceded or coincided with deployment-oriented work (participant data collection setup in Phase 5; the `deployment_prep` branch stabilization in Phase 7).

4. **AI-assisted development**: Starting in Phase 6, commit messages begin including `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`, indicating the transition to AI-assisted implementation for feature work and refactoring. This is consistent with the more structured, complete commit messages observed in Phase 6 and 7 versus the exploratory `wip`/`'` messages of earlier phases.

5. **Schema versioning discipline**: Each breaking change to the feature row schema was accompanied by a `FeatureVersion` bump, ensuring that stored features could be correlated with the schema version that produced them.

---

## Document Metadata

- **Generated:** 2026-06-14 (updated from 2026-06-10 original)
- **Branch:** `deployment_prep`
- **Total commits surveyed:** ~125
- **Date range:** 2026-01-13 to 2026-06-14
- **Methodology:** Full `git log` sweep with commit subject, body, and date; cross-referenced against `docs/active/ARCHITECTURE.md`, `docs/active/EXTRACTOR.md`, and source tree structure.
