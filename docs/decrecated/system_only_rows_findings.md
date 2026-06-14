# System-Only Rows — Investigation Findings

**Date:** 2026-06-11  
**DBs analysed:** All 15 (DataBase/1–15)  
**Profile used for counts:** W30_S15 (most granular; W60_S30 and W120_S60 confirm proportionally)

---

## 1. What these rows are

A "system-only" row is a feature window where every source collector reported zero events **except** the system resource collector:

```
source_counts_json = {"application": 0, "session": 0, "network": 0, "system": 37}
                                                                          or 38
```

They are **not** drain rows (pre-sleep zeros) — they have `has_system_data = 1` and non-zero CPU. They pass the current training quality filter:

```sql
WHERE has_collection_gap = 0 AND in_warm_up = 0 AND has_system_data = 1
```

---

## 2. The system tick rate fingerprint

The system count is always 37 or 38 for W30_S15 windows. Across profiles:

| Profile | Window (s) | Observed system count | Ticks/sec |
|---------|-----------|----------------------|-----------|
| W30_S15 | 30 | 37 or 38 | 1.25 |
| W60_S30 | 60 | 75 | 1.25 |
| W120_S60 | 120 | 150 | 1.25 |

The ratio is exactly proportional (37.5 × 1 : 2 : 4). This means `SystemResourceCollector` fires at a fixed **800 ms interval** during an active session. The ±1 alternation (37/38) is a sliding-window boundary effect — whether the window captures 37 or 38 complete ticks depends on sub-second phase alignment.

---

## 3. How this arises from raw-signal replay

The four collectors have fundamentally different triggering modes:

| Collector | Mode | Fires when |
|-----------|------|-----------|
| `SystemResourceCollector` | **Interval poll** | Every 800 ms, unconditionally |
| `ApplicationUsageCollector` | **Event hook** | `EVENT_SYSTEM_FOREGROUND` — only on foreground window change |
| `SessionStateCollector` | **Event hook** | Lock / unlock / logon / logoff |
| `NetworkContextCollector` | **Event hook** | Network adapter state change |

When a user stays in **one application without switching** for more than one window duration:

- No `EVENT_SYSTEM_FOREGROUND` fires → `application = 0`
- No lock/unlock fires → `session = 0`
- No network change fires → `network = 0`
- SystemResourceCollector keeps ticking at 800 ms → `system = 37–38`

The raw signal file for that period contains only `SystemResourceTick` events. The replay faithfully reprocesses them — producing a window with `has_app_data = 0` and `active_work_ratio = 0`, even though the user was actively working.

**This is not a replay artifact or a bug.** It is the inherent behaviour of hook-based app collection: if the user does not switch focus, the collector produces no events.

---

## 4. The training impact

```
active_work_ratio = 0  ← for 100% of system-only rows (all 11,210 in DB1)
```

`active_work_ratio` is computed from foreground app events. With `application = 0`, there are no events to compute it from, so it defaults to 0.

This creates a **misleading signal**: CPU activity is real (avg 0.68 in DB1), display is on, machine is not locked — but the model sees `awr = 0`, which in all other rows means "user is not at the computer." A classifier trained on this data cannot distinguish between:

- User reading a long document (no app switch, CPU moderate) → `awr = 0` ← system-only row
- User away from desk with screen on → `awr = 0` ← also in the data

### Prevalence

| DB | Clean W30_S15 rows | System-only | % |
|----|-------------------|-------------|---|
| 1  | 24,988 | 11,210 | **44.9%** |
| 2  | 9,798  | 1,365  | 13.9% |
| 3  | 40,972 | 1,322  | 3.2% |
| 4  | 6,008  | 853    | 14.2% |
| 5  | 47,193 | 389    | 0.8% |
| 6  | 20,353 | 163    | 0.8% |
| 7  | 34,107 | 4,312  | 12.6% |
| 8  | 20,605 | 3,072  | 14.9% |
| 9  | 14,212 | 1,511  | 10.6% |
| 10 | 59,228 | 4,390  | 7.4% |
| 11 | 21,591 | 57     | 0.3% |
| 12 | 11,732 | 80     | 0.7% |
| 13 | 63,106 | 1,157  | 1.8% |
| 14 | 8,261  | 28     | 0.3% |
| 15 | 22,517 | 8,151  | **36.2%** |

DB1 and DB15 are outliers — these participants spent a large fraction of their sessions focused in one app.

### Block duration profile (DB1, 642 runs)

| Duration | Run count |
|----------|-----------|
| < 60 s   | 218 (34%) |
| 1 – 5 min | 274 (43%) |
| > 5 min  | 150 (23%) |

- Average block: **244 s (≈ 4 min)**
- Longest block: **4,050 s (≈ 67 min)**

The long tail matters: 23% of runs exceed 5 minutes. These are real sustained-focus sessions, not noise.

---

## 5. What cannot be inferred from these rows

- Whether the user was **actively engaged** (reading, watching, coding) or **passively present** (walked away, left screen on)
- Which application was in focus — `app_count = 0` means no foreground *change*, not no foreground app
- A reliable `active_work_ratio` — the formula is undefined without app events

---

## 6. Options for handling in training

| Option | Description | Trade-off |
|--------|-------------|-----------|
| **Filter out** | Add `AND has_app_data = 1` to clean filter | Removes 0.3–44.9% of clean rows depending on participant; loses all sustained-focus data |
| **Keep, add flag** | Add `has_sustained_focus INTEGER` column — 1 when `app = session = network = 0, system > 0` | Lets model learn separately; requires feature engineering |
| **Keep, treat as unknown** | Leave in training with `awr = 0`; accept that model conflates idle and focused | Introduces systematic label noise |
| **Impute awr** | For system-only rows, estimate `awr` from CPU/RAM activity index | Lossy — no ground truth to validate against |

The safest short-term action is to **add `has_sustained_focus` as a flag** (same pattern as `has_collection_gap` and `in_warm_up`) and let downstream consumers decide whether to include or exclude.

---

## 7. Proposed schema addition

```sql
ALTER TABLE feature_rows ADD COLUMN has_sustained_focus INTEGER NOT NULL DEFAULT 0;

UPDATE feature_rows
SET has_sustained_focus = 1
WHERE json_extract(source_counts_json, '$.application') = 0
  AND json_extract(source_counts_json, '$.session')     = 0
  AND json_extract(source_counts_json, '$.network')     = 0
  AND json_extract(source_counts_json, '$.system')      > 0
  AND has_collection_gap = 0
  AND in_warm_up = 0;
```

Extended clean filter (conservative — excludes sustained-focus rows):

```sql
WHERE has_collection_gap = 0
  AND in_warm_up = 0
  AND has_system_data = 1
  AND has_sustained_focus = 0
```

Extended clean filter (inclusive — keeps all interpretable rows):

```sql
WHERE has_collection_gap = 0
  AND in_warm_up = 0
  AND has_system_data = 1
```
