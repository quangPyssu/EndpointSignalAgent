# Zero Application Signal — Root Cause Analysis

**Date:** 2026-06-11  
**DBs analysed:** 1–14 (all 14 migrated DBs; W30_S15 profile throughout unless noted)  
**Total clean W30_S15 rows:** 382,154

---

## Key finding: 81% of W30_S15 rows have zero app events

```
Clean W30_S15 rows with application=0, network=0:  311,077  (81.4%)
Clean W30_S15 rows with application>0 or network>0:  71,077  (18.6%)
```

The majority of W30_S15 data has no app or network signal. This is not a data quality problem — it reflects how event-driven collectors work and when machines are left idle.

---

## 1. Why `application = 0` in W30_S15

### The 30-second window is too short for AppSwitchRate

`AppSwitchRate` is a **pre-aggregated** signal emitted once per 60-second collector window. For W60_S30 and W120_S60, at least one (usually two) `AppSwitchRate` events always fall within the feature window:

| Profile | Window | Min observed `application` | Zero-app possible? |
|---------|--------|---------------------------|-------------------|
| W30_S15 | 30s    | 0                         | Yes               |
| W60_S30 | 60s    | 2                         | No                |
| W120_S60| 120s   | 3                         | No                |

**The system-only and session-only phenomena are structurally limited to W30_S15.** W60_S30 windows always have at least 2 app events (two `AppSwitchRate` events with `switches=0` during sustained focus), so `application` is never 0 for those profiles.

Consequence: any analysis or filter based on `application=0` only affects W30_S15 training data.

### Why a W60_S30 window during sustained focus still has `application=2`

`AppSwitchRate` fires every 60s with `switches=0` if the user didn't switch. The 60s feature window overlaps two consecutive AppSwitchRate periods, capturing both their events. Both events have `switches=0` and `has_app_data=1` (the extractor counts their presence). The user can be in pure sustained focus and W60/W120 will still record `application≥2, has_app_data=1`.

Note: 88 W60_S30 rows in DB1 have `app_cnt=2, switches=0, has_app_data=0` — the events were counted in `source_counts_json` but didn't qualify as "app data" for feature computation. This is a minor edge case (0.7% of W60 rows).

---

## 2. Sub-types of zero-app W30_S15 windows

Five distinct states produce `application=0, network=0`:

| Sub-type | Classifier | Rows (all DBs) | % of clean |
|---|---|---:|---:|
| `sys_pure` | `sess=0` | 29,909 | 7.8% |
| `sess_idle0` | `sess>0, idle=0s` | 9,358 | 2.4% |
| `sess_idle1_59` | `sess>0, idle 1-59s` | 43,542 | 11.4% |
| `sess_idle60_300` | `sess>0, idle 60-299s` | 43,537 | 11.4% |
| `sess_idle300plus` | `sess>0, idle ≥300s` | 184,731 | 48.3% |
| **Total zero-app** | | **311,077** | **81.4%** |

The `idle` field is `idle_bucket_mean_sec` from `features_json`.

---

## 3. What drives `session > 0` when `application = 0`

Of 8,991 `app=0, net=0, sess>0` rows in DB1: **8,990 have `lock_count=0, unlock_count=0`**. The session count is driven entirely by **`IdleSample`** events — not lock or unlock:

```
Signal Catalog:  IdleSample | StateSample | bucket change (adaptive)
  payload: idleMs, idleBucketSec, idleStatus, userPresence, presenceSource
```

`IdleSample` fires each time the idle timer crosses a bucket boundary (e.g., 5s → 30s → 60s → 300s). It provides `idle_bucket_mean_sec` and `presence_present_ratio` even when no app events fire. One session event = one bucket crossing.

When `sess=0`, the idle clock never crossed a boundary during the window — the user was interacting continuously (the "most actively working" state), or the idle clock was already stuck at 0.

---

## 4. Sub-type profiles

### 4.1 Compute activity (GPU and CPU)

GPU and CPU rates cleanly separate the sub-types:

| Sub-type | GPU > 5% | CPU > 10% | Avg GPU | Avg CPU (DB1) |
|---|---:|---:|---:|---:|
| `sys_pure` | **81.9%** | 52.1% | — | 0.684 |
| `sess_idle0` | **68.5%** | 55.8% | — | 0.763 |
| `sess_idle1_59` | 52.5% | 44.2% | — | 0.773 |
| `sess_idle60_300` | 40.0% | 32.8% | — | 1.005 |
| `sess_idle300plus` | **8.2%** | 20.6% | — | 0.847 |

GPU percentile distribution (all DBs combined, W30_S15):

| Sub-type | mean | p25 | p50 | p75 |
|---|---:|---:|---:|---:|
| `sys_pure` | 26.3% | 5.9% | 11.2% | 43.2% |
| `sess_idle0` | 22.2% | 3.7% | 10.5% | 33.6% |
| `sess_idle1_59` | 16.6% | 0.6% | 6.2% | 25.3% |
| `sess_idle60_300` | 12.5% | 0.0% | 1.4% | 19.6% |
| `sess_idle300plus` | **2.5%** | **0.0%** | **0.01%** | **0.1%** |

**GPU is the strongest single discriminator.** `sys_pure` median GPU = 11.2%; `sess_idle300plus` median GPU = 0.01%. An order-of-magnitude difference at the median.

### 4.2 Block duration (consecutive-run analysis)

How long does each sub-type persist uninterrupted? (W30_S15, step=15s, all DBs)

| Sub-type | Runs | Median | Mean | p90 | Max |
|---|---:|---:|---:|---:|---:|
| `sys_pure` | 3,326 | 60s | 135s | 300s | 5,760s (96 min) |
| `sess_idle0` | 4,290 | **30s** | 33s | 45s | 75s |
| `sess_idle1_59` | 9,885 | 45s | 66s | 135s | 1,005s |
| `sess_idle60_300` | 5,387 | 90s | 121s | 255s | 285s |
| `sess_idle300plus` | 1,533 | 315s | 1,808s | 2,580s | 224,325s (62 hr) |

`sess_idle0` median = 30s (single window). These are transient: one window where the idle clock momentarily crossed a threshold before resetting. They are almost always isolated single windows.

`sys_pure` median = 60s (4 consecutive windows), mean = 135s (9 windows), max 96 minutes. These are genuine sustained-focus sessions.

`sess_idle300plus` max = 62 hours — a machine left running overnight or over a weekend with presence detection still active.

### 4.3 Presence during extended idle

`presence_present_ratio` is high across all sub-types, including long idle:

| Sub-type | Avg presence |
|---|---:|
| `sys_pure` | — (no IdleSample to carry presence) |
| `sess_idle0` | 0.9996 |
| `sess_idle1_59` | 0.856 |
| `sess_idle60_300` | ~0.97 |
| `sess_idle300plus` | ~0.94 |

Presence remains high even during multi-hour idle because the presence sensor (camera/proximity hardware) continues to detect the user or nearby surfaces. A machine left on a desk overnight with a presence sensor facing the room will report `presence_present_ratio ≈ 1.0` for every window.

**Presence is not reliable for distinguishing "user is active" from "machine is unattended."** GPU is a better signal.

### 4.4 Idle duration breakdown for `sess_idle60plus` (DB-level)

Of the `sess_idle60plus` rows per DB (idle ≥ 60s):

| DB | idle 60–299s | idle 5min–1hr | idle > 1hr |
|---|---:|---:|---:|
| 1  | 1,616 | 1,196 | 146 |
| 2  | 1,553 | 1,687 | 151 |
| 3  | 5,666 | 4,060 | 3,093 |
| 4  | 365 | 1,617 | 401 |
| 5  | 949 | 3,432 | **40,127** |
| 6  | 7,226 | 7,733 | 20 |
| 7  | 5,728 | 6,262 | 89 |
| 8  | 1,611 | 2,740 | 2,855 |
| 9  | 2,245 | 2,540 | 688 |
| 10 | 3,367 | 9,240 | **29,792** |
| 11 | 7,726 | 7,033 | 0 |
| 12 | 1,716 | 7,262 | 798 |
| 13 | 2,420 | 6,267 | **40,264** |
| 14 | 1,349 | 1,740 | 3,498 |

DB5, DB10, DB13 have very large "idle > 1hr" populations (40k+ rows). These participants' machines ran for extended periods without user interaction — likely overnight sessions captured in the raw signal files. The `sess_idle300plus` bucket's enormous size (184,731 rows, 48.3% of all clean data) is dominated by these long-idle machines.

---

## 5. The `app_top1_share = 0` problem

Even when `application > 0`, `app_top1_share` can still be exactly 0. This is separate from the zero-app phenomenon and affects W60/W120 rows that have AppSwitchRate events but no `AppDwell`.

### Root cause: AppDwell is trailing-edge

`app_top1_share = top1_app_ms / total_dwell_ms`, sourced from `AppDwell` events.

`AppDwell` fires only when **a foreground dwell ends** (user switches away, or shutdown flush). A sustained open dwell — the user focused in one application without switching — produces zero `AppDwell` events for the entire duration.

```
Timeline: user is focused on one app, 13 minutes, no switching

  Each 30s window:
    SystemResourceTick ×37   → recorded in raw file every 800ms
    AppDwell (current app)   → NOT recorded; dwell is still open
    ForegroundAppChanged     → NOT recorded; no switch happened

  Result: app_top1_share = 0/0 → 0, has_app_data = 0
```

### Data confirmation (DB1, ~14:15 UTC)

```
14:15:15  app=12  top1=0.77  dwell_max=11,046ms  ← last window with real dwell commit
14:15:30  app=6   top1=0                          ← transition: app events draining
14:16:15  app=0   top1=0  has_app=0               ← sustained-focus block begins
  ...  13 minutes of system=37-38, app=0  ...
14:29:15  app=4   top1=0.96  dwell_max=19,846ms   ← user finally switches
```

At 14:29:15, `dwell_max = 19,846ms (~20s)`. This is a brief new dwell committed at the moment the user alt-tabbed — not an accumulation of the 13-minute focus period. The entire 13-minute session left no trace in `AppDwell`.

### The value `0` is not "no app" — it means "no AppDwell commit"

`app_top1_share = 0` covers all three of:
1. User maximally focused on one app (dwell still open)
2. Machine keyboard-idle or unattended
3. Session locked

Without `idle_bucket_mean_sec` and `gpu_usage_mean`, these are indistinguishable.

---

## 6. Signal taxonomy — complete picture

```
W30_S15 clean rows (all DBs): 382,154

├── application > 0  (18.6%)  ← real app events; normal training data
│
└── application = 0, network = 0  (81.4%)
    ├── has_system_data = 0  → drain or WMI failure; excluded already
    │
    └── has_system_data = 1  (311,077 rows)
        │
        ├── sys_pure  (sess=0)  [29,909 — 9.6% of zero-app]
        │   GPU: p50=11.2%  Block median: 60s
        │   Interpretation: active user, no app switch, idle clock continuous near 0
        │   Trainability: HIGH — real work signal, just no AppDwell
        │
        ├── sess_idle0  (sess>0, idle=0)  [9,358 — 3.0%]
        │   GPU: p50=10.5%  Block median: 30s (mostly single windows)
        │   Interpretation: active user; idle clock momentarily crossed boundary
        │   Trainability: HIGH — same as sys_pure, just with idle telemetry
        │
        ├── sess_idle1_59  (idle 1–59s)  [43,542 — 14.0%]
        │   GPU: p50=6.2%  Block median: 45s
        │   Interpretation: transition zone — becoming idle or resuming activity
        │   Trainability: MIXED — boundary windows; label noise in either direction
        │
        ├── sess_idle60_300  (idle 60–299s)  [43,537 — 14.0%]
        │   GPU: p50=1.4%  Block median: 90s
        │   Interpretation: user present (pres≈0.97) but keyboard-idle 1–5 min
        │   Trainability: LOW — watching/reading/meeting; cannot confirm active work
        │
        └── sess_idle300plus  (idle ≥300s)  [184,731 — 59.4%]
            GPU: p50=0.01%  Block median: 315s, max: 62hr
            Interpretation: machine unattended; long idle, background processes only
            Trainability: VERY LOW — machine effectively idle; useful only as "not working"
```

---

## 7. Revised filter recommendations

### Current filter (unchanged)
```sql
WHERE has_collection_gap = 0
  AND in_warm_up = 0
  AND json_extract(features_json, '$.has_system_data') = 1
```
Keeps all 311,077 zero-app rows including 184,731 machine-abandoned rows. Only 18.6% of training data has real app events.

### Conservative: exclude machine-idle rows
```sql
WHERE has_collection_gap = 0
  AND in_warm_up = 0
  AND json_extract(features_json, '$.has_system_data') = 1
  AND NOT (
    json_extract(source_counts_json, '$.application') = 0
    AND json_extract(source_counts_json, '$.network') = 0
    AND json_extract(features_json, '$.idle_bucket_mean_sec') >= 300
  )
```
Removes 184,731 rows (48.3% of clean). Keeps all active-user patterns including sustained-focus.

### Recommended: active-user filter with GPU threshold
```sql
WHERE has_collection_gap = 0
  AND in_warm_up = 0
  AND json_extract(features_json, '$.has_system_data') = 1
  AND (
    json_extract(source_counts_json, '$.application') > 0
    OR json_extract(features_json, '$.gpu_usage_mean') > 5
    OR (
      json_extract(source_counts_json, '$.application') = 0
      AND json_extract(features_json, '$.idle_bucket_mean_sec') < 60
    )
  )
```
Keeps rows where there is evidence of user activity: real app events, GPU activity, or low-idle sustained focus. Excludes machine-idle and most passive-present rows.

---

## 8. Proposed schema additions

### `has_sustained_focus` (already proposed in `system_only_rows_findings.md`)
Flags the `sys_pure` sub-type — active user, continuous keyboard use, no app switch.

```sql
ALTER TABLE feature_rows ADD COLUMN has_sustained_focus INTEGER NOT NULL DEFAULT 0;
UPDATE feature_rows SET has_sustained_focus = 1
WHERE json_extract(source_counts_json, '$.application') = 0
  AND json_extract(source_counts_json, '$.session')     = 0
  AND json_extract(source_counts_json, '$.network')     = 0
  AND json_extract(source_counts_json, '$.system')      > 0
  AND has_collection_gap = 0 AND in_warm_up = 0;
```

### `is_machine_idle`
Flags long-idle unattended windows.

```sql
ALTER TABLE feature_rows ADD COLUMN is_machine_idle INTEGER NOT NULL DEFAULT 0;
UPDATE feature_rows SET is_machine_idle = 1
WHERE json_extract(source_counts_json, '$.application') = 0
  AND json_extract(source_counts_json, '$.network')     = 0
  AND json_extract(source_counts_json, '$.session')     > 0
  AND json_extract(features_json, '$.idle_bucket_mean_sec') >= 300
  AND has_collection_gap = 0 AND in_warm_up = 0;
```

These two flags together cover the two extremes of zero-app rows:
- `has_sustained_focus = 1` → user is actively working, just not switching apps
- `is_machine_idle = 1` → machine is unattended

Rows with neither flag set are the ambiguous transition and passive-focus populations.

---

## 9. Cross-DB sub-type breakdown (W30_S15)

| DB | Clean | sys_pure | sess_idle0 | idle1_59 | idle60_300 | idle300+ |
|---|---:|---:|---:|---:|---:|---:|
| 1  | 24,988 | 11,210 | 1,953 | 4,080 | 1,616 | 1,488 |
| 2  | 9,798  | 1,365  | 683   | 2,194 | 1,553 | 1,838 |
| 3  | 40,972 | 1,322  | 1,075 | 5,847 | 5,666 | 7,152 |
| 4  | 6,008  | 853    | 216   | 463   | 365   | 2,018 |
| 5  | 47,193 | 389    | 232   | 1,065 | 949   | 43,559 |
| 6  | 20,353 | 163    | 231   | 3,828 | 7,226 | 7,753 |
| 7  | 34,107 | 4,312  | 1,799 | 6,489 | 5,728 | 6,351 |
| 8  | 20,605 | 3,072  | 1,133 | 4,451 | 1,611 | 5,595 |
| 9  | 14,212 | 1,511  | 341   | 2,133 | 2,245 | 3,228 |
| 10 | 59,228 | 4,390  | 1,007 | 3,834 | 3,367 | 39,032 |
| 11 | 21,591 | 57     | 107   | 5,508 | 7,726 | 7,033 |
| 12 | 11,732 | 80     | 57    | 534   | 1,716 | 8,060 |
| 13 | 63,106 | 1,157  | 483   | 2,412 | 2,420 | 46,531 |
| 14 | 8,261  | 28     | 41    | 704   | 1,349 | 5,241 |

DB1 stands out for `sys_pure` (11,210 — 44.9% of its clean rows): this participant spends the most time in sustained focused work without switching apps. DB5/DB10/DB13 are dominated by `sess_idle300plus` — machines left running idle for long periods.
