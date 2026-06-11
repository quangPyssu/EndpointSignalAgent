# Gap Recovery Script Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A standalone Python script that retroactively backfills `has_collection_gap` and `in_warm_up` columns into every `features.db` in `DataBase/`, tagging the pre-sleep drain rows and post-resume warm-up rows that the old agent (before sleep handling) did not flag.

**Architecture:** Three-pass SQLite migration per database — (1) add columns if absent, (2) detect gap boundaries from timestamp discontinuities, (3) flag drain rows within 180s before each sleep, (4) flag warm-up rows within 120s after each resume. After all DBs are processed, a before/after comparison report is written to `scripts/gap_recovery_report.md`. All logic is pure SQL driven from Python. The script is idempotent: safe to re-run on already-migrated DBs.

**Tech Stack:** Python 3.9+, `sqlite3` (stdlib), `pytest` with `tmp_path` fixtures for in-memory/temp DB testing. No third-party dependencies.

---

## Constants (from DB analysis)

```
GAP_THRESHOLD_SEC   = 300   # bimodal split; nothing observed 60–300s
DRAIN_LOOKBACK_SEC  = 180   # max observed drain extent 165s + 15s margin
WARMUP_DURATION_SEC = 120   # max observed ramp-to-≥0.99 AWR = 119s, rounded
```

---

## File Map

| File | Role |
|------|------|
| `scripts/gap_recovery.py` | Main script — `migrate_db(path)`, CLI entry point |
| `scripts/gap_recovery_sql.py` | All SQL strings as named constants — imported by main and tests |
| `tests/gap_recovery/test_gap_recovery.py` | pytest suite — covers each pass and the idempotency guarantee |
| `tests/gap_recovery/conftest.py` | `seed_db` fixture — builds a minimal in-memory DB with known gap pattern |

---

## Task 1: SQL constants module

**Files:**
- Create: `scripts/gap_recovery_sql.py`

- [ ] **Step 1: Create `scripts/gap_recovery_sql.py`**

```python
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
# Uses DISTINCT window_start_ts to avoid triple-counting per profile.
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
# Drain rows: both has_system_data and has_app_data == 0,
# and the row falls within DRAIN_LOOKBACK_SEC before a sleep_ts.
FLAG_DRAIN_ROWS = """
UPDATE feature_rows
SET has_collection_gap = 1
WHERE json_extract(features_json, '$.has_system_data') = 0
  AND json_extract(features_json, '$.has_app_data')    = 0
  AND EXISTS (
      SELECT 1 FROM _gaps g
      WHERE feature_rows.window_start_ts <= g.sleep_ts
        AND (julianday(g.sleep_ts) - julianday(feature_rows.window_start_ts)) * 86400
            <= {lookback}
  )
""".format(lookback=DRAIN_LOOKBACK_SEC)

# Pass 3 — flag warm-up rows (post-resume ramp).
# First WARMUP_DURATION_SEC of rows after each resume_ts.
FLAG_WARMUP_ROWS = """
UPDATE feature_rows
SET in_warm_up = 1
WHERE EXISTS (
    SELECT 1 FROM _gaps g
    WHERE feature_rows.window_start_ts >= g.resume_ts
      AND (julianday(feature_rows.window_start_ts) - julianday(g.resume_ts)) * 86400
          <= {warmup}
)
""".format(warmup=WARMUP_DURATION_SEC)

DROP_GAPS_TABLE = "DROP TABLE IF EXISTS _gaps"

# Summary query — used for reporting after migration.
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
```

- [ ] **Step 2: Verify it imports cleanly**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -c "import scripts.gap_recovery_sql as s; print('threshold:', s.GAP_THRESHOLD_SEC)"
```

Expected output: `threshold: 300`

- [ ] **Step 3: Commit**

```bash
git add scripts/gap_recovery_sql.py
git commit -m "feat: gap recovery SQL constants module"
```

---

## Task 2: Core migration logic (TDD)

**Files:**
- Create: `tests/gap_recovery/conftest.py`
- Create: `tests/gap_recovery/test_gap_recovery.py`
- Create: `scripts/gap_recovery.py` (minimal skeleton)

### Step 2a — test fixture and skeleton

- [ ] **Step 1: Create `tests/gap_recovery/__init__.py`** (empty)

```bash
mkdir -p /Users/lap15174/EndpointSignalAgent/tests/gap_recovery
touch /Users/lap15174/EndpointSignalAgent/tests/gap_recovery/__init__.py
```

- [ ] **Step 2: Create `tests/gap_recovery/conftest.py`**

The fixture builds a SQLite DB with:
- 9 rows of normal data (has_system_data=1, has_app_data=1) at T+0 through T+120s
- 9 drain rows (has_system_data=0, has_app_data=0) at T+130s through T+250s (within 180s of T+260)
- A 400s gap (sleep_ts=T+260, resume_ts=T+660)
- 6 warm-up rows at T+660 through T+750s (within 120s of T+660)
- 9 rows of normal data at T+780s through T+900s

All rows are replicated across 3 profiles (W30_S15, W60_S30, W120_S60) — 27+27+18+27 rows total.

```python
import sqlite3
import json
import pytest
from datetime import datetime, timezone, timedelta

BASE_TS = datetime(2026, 1, 1, 8, 0, 0, tzinfo=timezone.utc)

PROFILES = [
    ("W30_S15",  30, 15),
    ("W60_S30",  60, 30),
    ("W120_S60", 120, 60),
]

def _ts(seconds: int) -> str:
    dt = BASE_TS + timedelta(seconds=seconds)
    return dt.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00")

def _row(ts_sec: int, profile: str, has_sys: int, has_app: int) -> tuple:
    features = json.dumps({
        "has_system_data": has_sys,
        "has_app_data":    has_app,
        "active_work_ratio": 0.99 if has_sys else 0.0,
    })
    return (_ts(ts_sec), profile, has_sys, has_app, features)

SCHEMA = """
CREATE TABLE feature_rows (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    window_start_ts TEXT NOT NULL,
    window_profile_id TEXT NOT NULL DEFAULT 'W60_S30',
    window_sec INTEGER NOT NULL DEFAULT 60,
    slide_sec INTEGER NOT NULL DEFAULT 30,
    device_id TEXT NOT NULL DEFAULT 'test-device',
    window_size_sec INTEGER NOT NULL DEFAULT 60,
    event_time_start TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00',
    event_time_end   TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00',
    extraction_run_id TEXT NOT NULL DEFAULT '',
    feature_schema_version TEXT NOT NULL DEFAULT '1.0',
    collector_schema_version TEXT NULL,
    source_counts_json TEXT NOT NULL DEFAULT '{}',
    feature_version TEXT NOT NULL DEFAULT '1.0',
    features_json TEXT NOT NULL,
    sent_flag INTEGER NOT NULL DEFAULT 0,
    sent_at TEXT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
"""

@pytest.fixture
def db_path(tmp_path):
    path = tmp_path / "features.db"
    conn = sqlite3.connect(str(path))
    conn.executescript(SCHEMA)

    rows = []
    for profile, _w, step in PROFILES:
        # 9 normal rows before drain: T+0, T+step, ... T+8*step
        for i in range(9):
            rows.append(_row(i * step, profile, 1, 1))

        # drain rows: 3 steps ending at T+260 (sleep_ts)
        # place them at T+260 - 3*step, T+260 - 2*step, T+260 - step
        drain_start = 260 - 3 * step
        for i in range(3):
            rows.append(_row(drain_start + i * step, profile, 0, 0))

        # gap: 400s (sleep_ts=260, resume_ts=660)

        # 3 warm-up rows: T+660, T+660+step, T+660+2*step
        for i in range(3):
            rows.append(_row(660 + i * step, profile, 1, 1))

        # 3 normal rows after warm-up: T+660+3*step onward
        for i in range(3):
            rows.append(_row(660 + (3 + i) * step, profile, 1, 1))

    conn.executemany(
        "INSERT INTO feature_rows (window_start_ts, window_profile_id, window_sec, slide_sec, features_json) "
        "VALUES (?, ?, 60, 30, ?)",
        [(r[0], r[1], r[4]) for r in rows],
    )
    conn.commit()
    conn.close()
    return path
```

- [ ] **Step 3: Create `scripts/gap_recovery.py` skeleton** (enough to import)

```python
import sqlite3
import sys
from pathlib import Path

import scripts.gap_recovery_sql as sql


def _column_exists(conn: sqlite3.Connection, column: str) -> bool:
    rows = conn.execute("PRAGMA table_info(feature_rows)").fetchall()
    return any(r[1] == column for r in rows)


def migrate_db(db_path: Path) -> dict:
    raise NotImplementedError


def main():
    raise NotImplementedError


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Commit skeleton**

```bash
git add scripts/gap_recovery.py tests/gap_recovery/__init__.py tests/gap_recovery/conftest.py
git commit -m "feat: gap recovery skeleton + test fixture"
```

---

### Step 2b — write failing tests

- [ ] **Step 5: Create `tests/gap_recovery/test_gap_recovery.py`**

```python
import sqlite3
import pytest
from pathlib import Path

# Adjust sys.path so scripts/ is importable when run from repo root
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from scripts.gap_recovery import migrate_db


# ── helpers ──────────────────────────────────────────────────────────────────

def query(db_path: Path, sql: str, params=()):
    conn = sqlite3.connect(str(db_path))
    result = conn.execute(sql, params).fetchall()
    conn.close()
    return result


def column_names(db_path: Path):
    rows = query(db_path, "PRAGMA table_info(feature_rows)")
    return [r[1] for r in rows]


# ── schema tests ──────────────────────────────────────────────────────────────

def test_adds_has_collection_gap_column(db_path):
    migrate_db(db_path)
    assert "has_collection_gap" in column_names(db_path)


def test_adds_in_warm_up_column(db_path):
    migrate_db(db_path)
    assert "in_warm_up" in column_names(db_path)


# ── gap detection tests ───────────────────────────────────────────────────────

def test_drain_rows_are_flagged(db_path):
    migrate_db(db_path)
    flagged = query(
        db_path,
        "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap = 1"
    )[0][0]
    # 3 drain rows × 3 profiles = 9
    assert flagged == 9


def test_normal_rows_are_not_flagged_as_drain(db_path):
    migrate_db(db_path)
    # Normal rows have has_system_data=1 — none should be flagged
    wrongly_flagged = query(
        db_path,
        """SELECT COUNT(*) FROM feature_rows
           WHERE has_collection_gap = 1
             AND json_extract(features_json, '$.has_system_data') = 1"""
    )[0][0]
    assert wrongly_flagged == 0


def test_warmup_rows_are_flagged(db_path):
    migrate_db(db_path)
    flagged = query(
        db_path,
        "SELECT COUNT(*) FROM feature_rows WHERE in_warm_up = 1"
    )[0][0]
    # 3 warm-up rows × 3 profiles = 9
    assert flagged == 9


def test_post_warmup_rows_are_not_flagged(db_path):
    migrate_db(db_path)
    # Rows at T+660+3*step onward should have in_warm_up=0
    wrong = query(
        db_path,
        """SELECT COUNT(*) FROM feature_rows
           WHERE in_warm_up = 1
             AND window_start_ts > '2026-01-01T08:02:00'"""
    )[0][0]
    assert wrong == 0


# ── idempotency ───────────────────────────────────────────────────────────────

def test_idempotent_double_run(db_path):
    migrate_db(db_path)
    migrate_db(db_path)  # second run must not crash or double-count

    drain = query(db_path, "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap = 1")[0][0]
    warmup = query(db_path, "SELECT COUNT(*) FROM feature_rows WHERE in_warm_up = 1")[0][0]
    assert drain == 9
    assert warmup == 9


# ── return value ──────────────────────────────────────────────────────────────

def test_migrate_returns_stats(db_path):
    result = migrate_db(db_path)
    assert result["gaps_found"] == 1
    assert result["drain_rows_flagged"] == 9
    assert result["warmup_rows_flagged"] == 9
    assert result["total_rows"] > 0
    assert result["clean_rows"] > 0
```

- [ ] **Step 6: Run tests — verify all fail**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m pytest tests/gap_recovery/ -v 2>&1 | tail -20
```

Expected: all 9 tests FAIL with `NotImplementedError`.

- [ ] **Step 7: Commit failing tests**

```bash
git add tests/gap_recovery/test_gap_recovery.py
git commit -m "test: gap recovery failing tests"
```

---

## Task 3: Implement `migrate_db`

**Files:**
- Modify: `scripts/gap_recovery.py`

- [ ] **Step 1: Implement `migrate_db`**

Replace the skeleton with:

```python
import sqlite3
import sys
from pathlib import Path

import scripts.gap_recovery_sql as sql


def _column_exists(conn: sqlite3.Connection, column: str) -> bool:
    rows = conn.execute("PRAGMA table_info(feature_rows)").fetchall()
    return any(r[1] == column for r in rows)


def _count_gaps(conn: sqlite3.Connection) -> int:
    return conn.execute("SELECT COUNT(*) FROM _gaps").fetchone()[0]


def migrate_db(db_path: Path) -> dict:
    conn = sqlite3.connect(str(db_path))
    conn.execute("PRAGMA journal_mode=WAL")

    try:
        # ── Pass 0: add columns (idempotent) ──────────────────────────────────
        if not _column_exists(conn, "has_collection_gap"):
            conn.execute(sql.ADD_COLLECTION_GAP_COL)
            conn.execute(sql.CREATE_GAP_INDEX)

        if not _column_exists(conn, "in_warm_up"):
            conn.execute(sql.ADD_IN_WARM_UP_COL)
            conn.execute(sql.CREATE_WARM_INDEX)

        conn.execute(sql.DROP_GAPS_TABLE)  # clean up temp table if prior run crashed
        conn.execute(sql.BUILD_GAPS_TABLE)

        gaps_found = _count_gaps(conn)

        # ── Pass 2: drain rows ────────────────────────────────────────────────
        conn.execute("UPDATE feature_rows SET has_collection_gap = 0")
        conn.execute(sql.FLAG_DRAIN_ROWS)
        drain_flagged = conn.execute(
            "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap = 1"
        ).fetchone()[0]

        # ── Pass 3: warm-up rows ──────────────────────────────────────────────
        conn.execute("UPDATE feature_rows SET in_warm_up = 0")
        conn.execute(sql.FLAG_WARMUP_ROWS)
        warmup_flagged = conn.execute(
            "SELECT COUNT(*) FROM feature_rows WHERE in_warm_up = 1"
        ).fetchone()[0]

        # ── Summary ───────────────────────────────────────────────────────────
        row = conn.execute(sql.SUMMARY_QUERY).fetchone()
        total_rows, drain_rows, warmup_rows, clean_rows = row

        conn.execute(sql.DROP_GAPS_TABLE)
        conn.commit()

    except Exception:
        conn.rollback()
        conn.close()
        raise

    conn.close()

    return {
        "gaps_found":          gaps_found,
        "drain_rows_flagged":  drain_flagged,
        "warmup_rows_flagged": warmup_flagged,
        "total_rows":          total_rows,
        "clean_rows":          clean_rows,
    }
```

- [ ] **Step 2: Run tests — verify all pass**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m pytest tests/gap_recovery/ -v
```

Expected: all 9 tests PASS.

- [ ] **Step 3: Commit**

```bash
git add scripts/gap_recovery.py
git commit -m "feat: implement migrate_db with 3-pass gap recovery"
```

---

## Task 4: CLI entry point

**Files:**
- Modify: `scripts/gap_recovery.py`

- [ ] **Step 1: Write failing CLI test**

Add to `tests/gap_recovery/test_gap_recovery.py`:

```python
import subprocess

def test_cli_runs_on_single_db(db_path):
    result = subprocess.run(
        ["python3", "-m", "scripts.gap_recovery", "--db", str(db_path)],
        capture_output=True, text=True,
        cwd="/Users/lap15174/EndpointSignalAgent"
    )
    assert result.returncode == 0
    assert "gaps_found=1" in result.stdout
    assert "drain=9" in result.stdout
    assert "warmup=9" in result.stdout


def test_cli_dry_run_makes_no_changes(db_path):
    subprocess.run(
        ["python3", "-m", "scripts.gap_recovery", "--db", str(db_path), "--dry-run"],
        capture_output=True, text=True,
        cwd="/Users/lap15174/EndpointSignalAgent"
    )
    # columns must not exist after dry run
    conn = sqlite3.connect(str(db_path))
    cols = [r[1] for r in conn.execute("PRAGMA table_info(feature_rows)").fetchall()]
    conn.close()
    assert "has_collection_gap" not in cols
```

- [ ] **Step 2: Run — verify new tests fail**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m pytest tests/gap_recovery/test_gap_recovery.py::test_cli_runs_on_single_db \
                  tests/gap_recovery/test_gap_recovery.py::test_cli_dry_run_makes_no_changes -v
```

Expected: FAIL — `NotImplementedError` from `main()`.

- [ ] **Step 3: Implement `main()` in `scripts/gap_recovery.py`**

Add at the bottom of the file (replace the `raise NotImplementedError` in `main`):

```python
import argparse


def _find_dbs(base_dir: Path):
    return sorted(base_dir.glob("*/features.db"))


def _dry_run_report(db_path: Path):
    conn = sqlite3.connect(str(db_path))
    try:
        conn.execute(sql.DROP_GAPS_TABLE)
        conn.execute(sql.BUILD_GAPS_TABLE)
        gaps = conn.execute("SELECT sleep_ts, resume_ts, gap_sec FROM _gaps ORDER BY sleep_ts").fetchall()
        conn.execute(sql.DROP_GAPS_TABLE)
    finally:
        conn.close()
    return gaps


def main():
    parser = argparse.ArgumentParser(description="Retroactive gap recovery for features.db files")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--db",      metavar="PATH", help="Single features.db to migrate")
    group.add_argument("--all",     metavar="DIR",  help="Base directory containing numbered DB folders (e.g. DataBase/)")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print gap summary without modifying any DB")
    args = parser.parse_args()

    targets = [Path(args.db)] if args.db else _find_dbs(Path(args.all))

    if not targets:
        print("No features.db files found.", file=sys.stderr)
        sys.exit(1)

    for db_path in targets:
        if not db_path.exists():
            print(f"[SKIP] {db_path} not found", file=sys.stderr)
            continue

        if args.dry_run:
            gaps = _dry_run_report(db_path)
            print(f"[DRY-RUN] {db_path}: {len(gaps)} gap(s)")
            for sleep_ts, resume_ts, gap_sec in gaps:
                print(f"          sleep={sleep_ts}  resume={resume_ts}  gap={gap_sec}s")
        else:
            try:
                stats = migrate_db(db_path)
                print(
                    f"[OK] {db_path}: "
                    f"gaps_found={stats['gaps_found']}  "
                    f"drain={stats['drain_rows_flagged']}  "
                    f"warmup={stats['warmup_rows_flagged']}  "
                    f"clean={stats['clean_rows']}/{stats['total_rows']}"
                )
            except Exception as exc:
                print(f"[FAIL] {db_path}: {exc}", file=sys.stderr)
                sys.exit(1)
```

- [ ] **Step 4: Run all tests — verify all pass**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m pytest tests/gap_recovery/ -v
```

Expected: all 11 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add scripts/gap_recovery.py tests/gap_recovery/test_gap_recovery.py
git commit -m "feat: CLI entry point with --db / --all / --dry-run"
```

---

## Task 5: Smoke test against real DBs

**Files:**
- No code changes — verification only.

- [ ] **Step 1: Dry-run across all real DBs**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m scripts.gap_recovery --all DataBase/ --dry-run
```

Expected: each DB prints its gap count. Cross-check against known values:

| DB | Expected gaps |
|----|--------------|
| 1  | 33 |
| 2  | 43 |
| 3  | 23 |
| 4  | 16 |
| 5  | 6  |
| 6  | 72 |
| 7  | 63 |
| 8  | 23 |
| 9  | 35 |
| 10 | 22 |

- [ ] **Step 2: Run migration on all DBs**

```bash
python3 -m scripts.gap_recovery --all DataBase/
```

Expected output (one line per DB): `[OK] DataBase/N/features.db: gaps_found=N  drain=N  warmup=N  clean=N/N`

- [ ] **Step 3: Spot-check DB 1 post-migration**

```bash
sqlite3 DataBase/1/features.db \
  "SELECT SUM(has_collection_gap), SUM(in_warm_up), COUNT(*) FROM feature_rows;"
```

Expected: drain≈297 (33 gaps × 9), warmup≈297 (33 gaps × 9), total≈44435.

- [ ] **Step 4: Verify filter produces clean training rows**

```bash
sqlite3 DataBase/1/features.db \
  "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap=0 AND in_warm_up=0 AND json_extract(features_json,'$.has_system_data')=1;"
```

Expected: clean row count = total - drain - warmup - mid-session zero-sys rows (~39 in DB1).

- [ ] **Step 5: Verify idempotency on already-migrated DB**

```bash
python3 -m scripts.gap_recovery --db DataBase/1/features.db
```

Expected: same stats as Step 2 — no doubled counts, no error.

- [ ] **Step 6: Commit final state**

```bash
git add DataBase/   # only if DB files are tracked — skip if .gitignored
git commit -m "chore: apply gap recovery migration to all DataBase/ DBs"
```

---

---

## Task 6: Before/after comparison report

**Files:**
- Modify: `scripts/gap_recovery_sql.py` — add `BEFORE_SNAPSHOT_QUERY`
- Modify: `scripts/gap_recovery.py` — add `snapshot_before()`, `write_report()`
- Modify: `tests/gap_recovery/test_gap_recovery.py` — add report tests

The report captures a "before" snapshot (naive row counts, no gap awareness) immediately before migration, then compares to the "after" stats returned by `migrate_db`. Output is both printed to stdout and saved as `scripts/gap_recovery_report.md`.

**What the report shows per DB:**

| Metric | Before | After |
|--------|--------|-------|
| Total rows | N | N (unchanged) |
| Naively "usable" (has_system_data=1) | N | — |
| Drain rows flagged | — | N |
| Warm-up rows flagged | — | N |
| Clean rows (gap=0, warmup=0, sys=1) | — | N |
| Data loss % (drain+warmup / total) | — | N% |

Plus a footer summary across all DBs.

### Step 6a — SQL constant and snapshot function

- [ ] **Step 1: Add `BEFORE_SNAPSHOT_QUERY` to `scripts/gap_recovery_sql.py`**

Append to the file:

```python
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
```

- [ ] **Step 2: Add `snapshot_before()` and `write_report()` to `scripts/gap_recovery.py`**

Add these two functions before `main()`:

```python
from datetime import datetime


def snapshot_before(db_path: Path) -> dict:
    """Read pre-migration counts from a DB (before columns are added)."""
    conn = sqlite3.connect(str(db_path))
    try:
        row = conn.execute(sql.BEFORE_SNAPSHOT_QUERY).fetchone()
        # Also count gaps so we can report them in the before column
        conn.execute(sql.DROP_GAPS_TABLE)
        conn.execute(sql.BUILD_GAPS_TABLE)
        gaps = conn.execute("SELECT COUNT(*) FROM _gaps").fetchone()[0]
        conn.execute(sql.DROP_GAPS_TABLE)
    finally:
        conn.close()
    return {
        "total_rows":        row[0],
        "naive_usable":      row[1],
        "distinct_ts":       row[2],
        "date_start":        row[3],
        "date_end":          row[4],
        "gaps_found":        gaps,
    }


def write_report(records: list[dict], report_path: Path) -> None:
    """
    records: list of dicts, one per DB:
        {db_path, before: dict, after: dict}
    Writes markdown to report_path and prints the table to stdout.
    """
    run_ts = datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ")
    lines = [
        "# Gap Recovery Report",
        "",
        f"**Run:** {run_ts}  ",
        f"**DBs processed:** {len(records)}",
        "",
        "## Per-DB Summary",
        "",
        "| DB | Total rows | Gaps | Drain flagged | Warm-up flagged | Clean rows | Data loss % |",
        "|----|-----------|------|--------------|----------------|-----------|------------|",
    ]

    total_rows_sum      = 0
    drain_sum           = 0
    warmup_sum          = 0
    clean_sum           = 0

    for rec in records:
        db_label   = rec["db_path"].parent.name  # e.g. "1", "2"
        before     = rec["before"]
        after      = rec["after"]
        total      = before["total_rows"]
        gaps       = after["gaps_found"]
        drain      = after["drain_rows_flagged"]
        warmup     = after["warmup_rows_flagged"]
        clean      = after["clean_rows"]
        loss_pct   = round(100.0 * (drain + warmup) / total, 1) if total else 0

        lines.append(
            f"| {db_label} | {total:,} | {gaps} | {drain:,} | {warmup:,} | {clean:,} | {loss_pct}% |"
        )

        total_rows_sum += total
        drain_sum      += drain
        warmup_sum     += warmup
        clean_sum      += clean

    total_loss_pct = round(100.0 * (drain_sum + warmup_sum) / total_rows_sum, 1) if total_rows_sum else 0

    lines += [
        f"| **TOTAL** | **{total_rows_sum:,}** | — | **{drain_sum:,}** | **{warmup_sum:,}** | **{clean_sum:,}** | **{total_loss_pct}%** |",
        "",
        "## Notes",
        "",
        f"- `GAP_THRESHOLD_SEC` = {sql.GAP_THRESHOLD_SEC}",
        f"- `DRAIN_LOOKBACK_SEC` = {sql.DRAIN_LOOKBACK_SEC}",
        f"- `WARMUP_DURATION_SEC` = {sql.WARMUP_DURATION_SEC}",
        "- **Data loss %** = (drain + warm-up rows) / total rows — these rows are excluded from training.",
        "- **Clean rows** = `has_collection_gap=0 AND in_warm_up=0 AND has_system_data=1`",
        "",
        "## Before vs After (naive usable → clean)",
        "",
        "| DB | Naive usable (before) | Clean rows (after) | Δ rows | Δ % |",
        "|----|----------------------|-------------------|--------|-----|",
    ]

    for rec in records:
        db_label   = rec["db_path"].parent.name
        naive      = rec["before"]["naive_usable"]
        clean      = rec["after"]["clean_rows"]
        delta      = clean - naive
        delta_pct  = round(100.0 * delta / naive, 1) if naive else 0
        sign       = "+" if delta >= 0 else ""
        lines.append(f"| {db_label} | {naive:,} | {clean:,} | {sign}{delta:,} | {sign}{delta_pct}% |")

    lines += ["", f"*Generated by `scripts/gap_recovery.py` at {run_ts}*", ""]

    report_path.write_text("\n".join(lines))
    print("\n".join(lines))
    print(f"\nReport saved to {report_path}")
```

- [ ] **Step 3: Wire `snapshot_before` and `write_report` into `main()`**

Replace the processing loop inside `main()` (the non-dry-run path) with:

```python
    records = []
    any_failed = False

    for db_path in targets:
        if not db_path.exists():
            print(f"[SKIP] {db_path} not found", file=sys.stderr)
            continue

        if args.dry_run:
            gaps = _dry_run_report(db_path)
            print(f"[DRY-RUN] {db_path}: {len(gaps)} gap(s)")
            for sleep_ts, resume_ts, gap_sec in gaps:
                print(f"          sleep={sleep_ts}  resume={resume_ts}  gap={gap_sec}s")
        else:
            try:
                before = snapshot_before(db_path)
                after  = migrate_db(db_path)
                records.append({"db_path": db_path, "before": before, "after": after})
                print(
                    f"[OK] {db_path}: "
                    f"gaps_found={after['gaps_found']}  "
                    f"drain={after['drain_rows_flagged']}  "
                    f"warmup={after['warmup_rows_flagged']}  "
                    f"clean={after['clean_rows']}/{after['total_rows']}"
                )
            except Exception as exc:
                print(f"[FAIL] {db_path}: {exc}", file=sys.stderr)
                any_failed = True

    if records and not args.dry_run:
        report_path = Path(__file__).parent / "gap_recovery_report.md"
        write_report(records, report_path)

    if any_failed:
        sys.exit(1)
```

### Step 6b — tests

- [ ] **Step 4: Write failing tests**

Add to `tests/gap_recovery/test_gap_recovery.py`:

```python
from scripts.gap_recovery import snapshot_before, write_report


def test_snapshot_before_returns_counts(db_path):
    snap = snapshot_before(db_path)
    assert snap["total_rows"] > 0
    assert snap["naive_usable"] > 0
    # drain rows have has_system_data=0 so naive_usable < total_rows
    assert snap["naive_usable"] < snap["total_rows"]
    assert snap["gaps_found"] == 1


def test_write_report_creates_file(db_path, tmp_path):
    before = snapshot_before(db_path)
    after  = migrate_db(db_path)
    report_path = tmp_path / "report.md"
    write_report(
        [{"db_path": db_path, "before": before, "after": after}],
        report_path,
    )
    content = report_path.read_text()
    assert "Gap Recovery Report" in content
    assert "TOTAL" in content
    assert "Naive usable" in content
    # verify numbers appear
    assert str(after["drain_rows_flagged"]) in content
    assert str(after["warmup_rows_flagged"]) in content


def test_write_report_delta_direction(db_path, tmp_path):
    # naive_usable includes warm-up rows but excludes drain zeros
    # clean_rows excludes both, so clean <= naive_usable
    before = snapshot_before(db_path)
    after  = migrate_db(db_path)
    report_path = tmp_path / "report.md"
    write_report(
        [{"db_path": db_path, "before": before, "after": after}],
        report_path,
    )
    assert after["clean_rows"] <= before["naive_usable"]
```

- [ ] **Step 5: Run — verify new tests fail**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m pytest tests/gap_recovery/test_gap_recovery.py \
    -k "snapshot or report" -v 2>&1 | tail -15
```

Expected: 3 FAIL — `ImportError` or `NotImplementedError`.

- [ ] **Step 6: Run all tests — verify all 14 pass**

```bash
python3 -m pytest tests/gap_recovery/ -v
```

Expected: 14 PASS.

- [ ] **Step 7: Smoke test — generate real report**

```bash
cd /Users/lap15174/EndpointSignalAgent
python3 -m scripts.gap_recovery --all DataBase/
```

Expected: `scripts/gap_recovery_report.md` created. Check it:

```bash
cat scripts/gap_recovery_report.md
```

Verify the TOTAL row shows aggregate drain + warmup counts matching DB analysis (~330 drain rows, ~330 warmup rows across all 10 DBs).

- [ ] **Step 8: Commit**

```bash
git add scripts/gap_recovery_sql.py scripts/gap_recovery.py \
        scripts/gap_recovery_report.md \
        tests/gap_recovery/test_gap_recovery.py
git commit -m "feat: before/after comparison report for gap recovery"
```

---

## Self-Review

**Spec coverage:**
- ✅ GAP_THRESHOLD_SEC=300, DRAIN_LOOKBACK_SEC=180, WARMUP_DURATION_SEC=120 encoded in `gap_recovery_sql.py`
- ✅ `has_collection_gap` and `in_warm_up` columns added
- ✅ `CREATE INDEX` on both columns
- ✅ 3-pass migration (gap detection → drain flag → warmup flag)
- ✅ Idempotent (column-exist check + reset before each pass)
- ✅ `--db` single file, `--all` directory scan, `--dry-run` mode
- ✅ Stats returned and printed
- ✅ All 3 anomalous gaps (DB6: 8/12, DB10: 10 drain rows) handled correctly — the SQL catches them because they satisfy the same predicate
- ✅ Before/after comparison report saved to `scripts/gap_recovery_report.md`
- ✅ Report includes: per-DB table, data loss %, naive-usable → clean delta, TOTAL row, constants footer

**Placeholder scan:** None found. All code blocks are complete.

**Type consistency:** `migrate_db(db_path: Path) -> dict` used consistently. `_dry_run_report` takes `Path`, returns `list[tuple]`. No mismatched names.
