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
        # 3 normal rows well before drain window
        for i in range(3):
            rows.append(_row(i * step, profile, 1, 1))

        # 3 drain rows: all within 180s of T+260 (sleep_ts)
        drain_start = 260 - 3 * step
        for i in range(3):
            rows.append(_row(drain_start + i * step, profile, 0, 0))

        # gap: sleep_ts=T+260, resume_ts=T+660 (400s)

        # 3 warm-up rows: T+660, T+660+step, T+660+2*step (all within 120s)
        for i in range(3):
            rows.append(_row(660 + i * step, profile, 1, 1))

        # 3 normal rows after warmup: start at T+900 (240s after resume, safely > 120s)
        for i in range(3):
            rows.append(_row(900 + i * step, profile, 1, 1))

    conn.executemany(
        "INSERT INTO feature_rows (window_start_ts, window_profile_id, window_sec, slide_sec, features_json) "
        "VALUES (?, ?, 60, 30, ?)",
        [(r[0], r[1], r[4]) for r in rows],
    )
    conn.commit()
    conn.close()
    return path
