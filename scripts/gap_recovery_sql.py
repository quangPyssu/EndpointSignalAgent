GAP_THRESHOLD_SEC   = 300
DRAIN_LOOKBACK_SEC  = 180
WARMUP_DURATION_SEC = 120

ADD_COLLECTION_GAP_COL = """
ALTER TABLE feature_rows ADD COLUMN has_collection_gap INTEGER NOT NULL DEFAULT 0
"""

ADD_IN_WARM_UP_COL = """
ALTER TABLE feature_rows ADD COLUMN in_warm_up INTEGER NOT NULL DEFAULT 0
"""

CREATE_GAP_INDEX = """
CREATE INDEX IF NOT EXISTS idx_gap  ON feature_rows(has_collection_gap)
"""

CREATE_WARM_INDEX = """
CREATE INDEX IF NOT EXISTS idx_warm ON feature_rows(in_warm_up)
"""

# Pass 1 — identify sleep/resume boundaries.
# Detects gaps per window_profile_id, then deduplicates on resume_ts so that
# a single sleep event shared across profiles is counted once.
# Returns rows: (sleep_ts TEXT, resume_ts TEXT, gap_sec INTEGER)
BUILD_GAPS_TABLE = """
CREATE TEMP TABLE _gaps AS
WITH dts AS (
    SELECT DISTINCT window_start_ts AS ts FROM feature_rows
),
lagged AS (
    SELECT ts,
           LAG(ts) OVER (ORDER BY ts) AS prev_ts,
           CAST(
               (julianday(ts) - julianday(LAG(ts) OVER (ORDER BY ts))) * 86400
           AS INTEGER) AS gap_sec
    FROM dts
)
SELECT prev_ts AS sleep_ts, ts AS resume_ts, gap_sec
FROM lagged
WHERE gap_sec > {threshold}
""".format(threshold=GAP_THRESHOLD_SEC)

# Pass 2 — flag drain rows (pre-sleep zeros).
FLAG_DRAIN_ROWS = """
UPDATE feature_rows
SET has_collection_gap = 1
WHERE json_extract(features_json, '$.has_system_data') = 0
  AND json_extract(features_json, '$.has_app_data')    = 0
  AND EXISTS (
      SELECT 1 FROM _gaps g
      WHERE feature_rows.window_start_ts <= g.sleep_ts
        AND CAST((julianday(g.sleep_ts) - julianday(feature_rows.window_start_ts)) * 86400 AS INTEGER)
            <= {lookback}
  )
""".format(lookback=DRAIN_LOOKBACK_SEC)

# Pass 3 — flag warm-up rows (post-resume ramp).
FLAG_WARMUP_ROWS = """
UPDATE feature_rows
SET in_warm_up = 1
WHERE EXISTS (
    SELECT 1 FROM _gaps g
    WHERE feature_rows.window_start_ts >= g.resume_ts
      AND CAST((julianday(feature_rows.window_start_ts) - julianday(g.resume_ts)) * 86400 AS INTEGER)
          <= {warmup}
)
""".format(warmup=WARMUP_DURATION_SEC)

DROP_GAPS_TABLE = "DROP TABLE IF EXISTS _gaps"

SUMMARY_QUERY = """
SELECT
    COUNT(*)                                                            AS total_rows,
    SUM(CASE WHEN has_collection_gap = 1 THEN 1 ELSE 0 END)           AS drain_rows,
    SUM(CASE WHEN in_warm_up = 1 THEN 1 ELSE 0 END)                   AS warmup_rows,
    SUM(CASE WHEN has_system_data_val = 1
              AND has_collection_gap = 0
              AND in_warm_up = 0 THEN 1 ELSE 0 END)                    AS clean_rows
FROM (
    SELECT has_collection_gap,
           in_warm_up,
           json_extract(features_json, '$.has_system_data') AS has_system_data_val
    FROM feature_rows
)
"""

# Taken BEFORE migration — counts naive usable rows (no gap awareness).
BEFORE_SNAPSHOT_QUERY = """
SELECT
    COUNT(*)                                                        AS total_rows,
    SUM(CASE WHEN json_extract(features_json,'$.has_system_data')=1
             THEN 1 ELSE 0 END)                                    AS naive_usable,
    (SELECT COUNT(DISTINCT window_start_ts) FROM feature_rows)     AS distinct_timestamps,
    MIN(window_start_ts)                                           AS date_start,
    MAX(window_start_ts)                                           AS date_end
FROM feature_rows
"""
