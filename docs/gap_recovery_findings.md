# Sleep / Collection-Gap Recovery — Findings from DB Analysis

**Date:** 2026-06-11  
**DBs analysed:** 1, 3, 10, 13 (chosen for row count diversity; all 15 DBs share the same 3 window profiles)

---

## 1. What the data looks like around a sleep boundary

### Pre-sleep drain

The agent's collectors stop before the OS suspends the window-generator loop.
This produces a cluster of fully-zero rows — `has_system_data = 0`, `has_app_data = 0`,
every metric = 0 — across **all three profiles simultaneously**, directly before
each large gap.

| DB | Gap (sec) | Pre-sleep last row (sleep_ts) | Drain rows | Drain extent (furthest zero before sleep_ts) |
|----|-----------|-------------------------------|-----------|----------------------------------------------|
| 1  | 399 299   | 2026-04-28T13:18:00Z          | 9         | 149 s                                        |
| 3  | 228 899   | 2026-05-12T14:22:00Z          | 9         | 150 s                                        |
| 10 | 178 320   | 2026-05-19T02:19:00Z          | 9         | 119 s                                        |
| 13 | 183 299   | 2026-05-08T22:38:00Z          | 9         | 165 s                                        |

**Drain row count is always 9** — structurally determined by the 3-profile combination
(W30_S15, W60_S30, W120_S60) and their respective step sizes. Not machine-dependent.

**Drain extent range: 119 – 165 s**  
→ Safe lookback constant: **180 s** before sleep_ts.

### Post-resume warm-up

`active_work_ratio` starts well below 1.0 on the first window after resume and ramps
up as the sliding window fills with real post-resume events.

| DB | W30_S15 (secs to ≥ 0.99) | W60_S30 (secs to ≥ 0.99) | W120_S60 (secs to ≥ 0.99) |
|----|--------------------------|--------------------------|---------------------------|
| 1  | 75 s                     | 60 s                     | 60 s                      |
| 3  | 89 s                     | 89 s                     | 119 s                     |
| 10 | 74 s                     | 89 s                     | 119 s                     |
| 13 | 74 s                     | 89 s                     | — (CPU spike skewed)      |

**Warm-up range: 60 – 119 s across all profiles**  
→ Safe warm-up constant: **120 s** after resume_ts.

> Note: the current live agent uses `WarmUpAfterResumeSec = 30`, which is too short
> for W60_S30 (needs 89 s) and W120_S60 (needs 119 s). Consider updating that config.

---

## 2. Zero-system rows are NOT a sleep indicator

339 / 44 435 rows in DB 1 (0.76 %) have `has_system_data = 0` during **normal
operation** — scattered throughout the day across all three profiles, not
clustered at gap boundaries.

| Period                      | Zero-sys rows | Total  | Rate  |
|-----------------------------|---------------|--------|-------|
| Within 5 min after any gap  | 12            | 1 017  | 1.2 % |
| Normal operation            | 327           | 43 418 | 0.75% |

**Conclusion:** zero-system rows that occur mid-session are a transient
WMI / PerfCounter polling failure. They should be dropped from training
(`WHERE has_system_data = 1`) but carry no information about sleep.

The only zero rows that indicate sleep are the **drain rows** — identified by
temporal proximity to a gap, not by zero content alone.

---

## 3. Recovery constants (for the migration script)

| Parameter              | Value  | Basis                                              |
|------------------------|--------|----------------------------------------------------|
| `GAP_THRESHOLD_SEC`    | 300    | Clear bimodal split; nothing between 60 s and 7 000 s |
| `DRAIN_LOOKBACK_SEC`   | 180    | Max observed drain = 165 s + 15 s margin           |
| `WARMUP_DURATION_SEC`  | 120    | Max observed ramp = 119 s, rounded up              |

---

## 4. Proposed column backfill logic

### Schema change (per DB)

```sql
ALTER TABLE feature_rows ADD COLUMN has_collection_gap INTEGER NOT NULL DEFAULT 0;
ALTER TABLE feature_rows ADD COLUMN in_warm_up         INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS idx_gap  ON feature_rows(has_collection_gap);
CREATE INDEX IF NOT EXISTS idx_warm ON feature_rows(in_warm_up);
```

### Pass 1 — build gap table

```sql
CREATE TEMP TABLE gaps AS
WITH dts AS (SELECT DISTINCT window_start_ts AS ts FROM feature_rows),
     lagged AS (
       SELECT ts,
              LAG(ts) OVER (ORDER BY ts) AS prev_ts,
              CAST((julianday(ts)-julianday(LAG(ts) OVER (ORDER BY ts)))*86400 AS INTEGER) AS gap_sec
       FROM dts
     )
SELECT prev_ts AS sleep_ts, ts AS resume_ts, gap_sec
FROM lagged
WHERE gap_sec > 300;
```

### Pass 2 — flag drain rows (`has_collection_gap = 1`)

Drain rows = zero rows within 180 s before sleep_ts:

```sql
UPDATE feature_rows
SET has_collection_gap = 1
WHERE json_extract(features_json, '$.has_system_data') = 0
  AND json_extract(features_json, '$.has_app_data')    = 0
  AND EXISTS (
    SELECT 1 FROM gaps g
    WHERE feature_rows.window_start_ts <= g.sleep_ts
      AND (julianday(g.sleep_ts) - julianday(feature_rows.window_start_ts)) * 86400
          <= 180
  );
```

### Pass 3 — flag warm-up rows (`in_warm_up = 1`)

First 120 s of rows after each resume_ts:

```sql
UPDATE feature_rows
SET in_warm_up = 1
WHERE EXISTS (
  SELECT 1 FROM gaps g
  WHERE feature_rows.window_start_ts >= g.resume_ts
    AND (julianday(feature_rows.window_start_ts) - julianday(g.resume_ts)) * 86400
        <= 120
);
```

### Scoring filter (unchanged from new-agent convention)

```sql
WHERE has_collection_gap = 0 AND in_warm_up = 0 AND has_system_data = 1
```

---

## 5. What cannot be recovered

- Whether a gap was **sleep vs. full shutdown vs. agent crash** — all look identical.
- The exact `PBT_APMSUSPEND` / `PBT_APMRESUMEAUTOMATIC` timestamps (boundary
  accuracy is ±15 s, the smallest step size).
- Per-gap reason metadata (`gapSec`, `suspendUtc`) that the new agent stores in
  `CollectionGapDetected` payload.
