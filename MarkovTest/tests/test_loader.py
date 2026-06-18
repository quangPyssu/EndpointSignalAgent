import pytest
import pandas as pd
import sqlite3, json, sys, os
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from loader import load_participant_db, load_all_participants, DEFAULT_PROFILES


# ── helpers ───────────────────────────────────────────────────────────────────

def _make_db(tmp_path, rows=2, profiles=("W60_S30",)):
    """Create a minimal features.db with feature_rows table."""
    db_path = tmp_path / "features.db"
    con = sqlite3.connect(str(db_path))
    con.execute("""
        CREATE TABLE feature_rows (
            id INTEGER PRIMARY KEY,
            window_start_ts TEXT,
            window_profile_id TEXT,
            features_json TEXT
        )
    """)
    for i, profile in enumerate(profiles):
        for j in range(rows):
            feats = {
                "has_app_data": 1, "cat_browser_ratio": 0.6,
                "cat_ide_ratio": 0.0, "cat_terminal_ratio": 0.0,
                "cat_comms_ratio": 0.0, "cat_office_ratio": 0.0,
                "cat_media_ratio": 0.0, "cat_gaming_ratio": 0.0,
                "cat_remoteaccess_ratio": 0.0, "cat_filemanager_ratio": 0.0,
                "cat_system_ratio": 0.0, "cat_other_ratio": 0.0,
                "locked_ratio": 0.0, "active_work_ratio": 0.6,
                "idle_bucket_mean_sec": 20.0, "idle_ge_300_ratio": 0.0,
                "has_idle_data": 1,
            }
            con.execute(
                "INSERT INTO feature_rows (window_start_ts, window_profile_id, features_json) VALUES (?, ?, ?)",
                (f"2026-01-{i+1:02d}T{j:02d}:00:00Z", profile, json.dumps(feats))
            )
    con.commit()
    con.close()
    return str(db_path)


# ── load_participant_db ───────────────────────────────────────────────────────

def test_load_participant_db_adds_participant_id(tmp_path):
    db = _make_db(tmp_path)
    df = load_participant_db(db, participant_id="007")
    assert (df["participant_id"] == "007").all()

def test_load_participant_db_adds_window_profile(tmp_path):
    db = _make_db(tmp_path, profiles=("W120_S60",))
    df = load_participant_db(db, participant_id="001")
    assert (df["window_profile"] == "W120_S60").all()

def test_load_participant_db_expands_features_json(tmp_path):
    db = _make_db(tmp_path)
    df = load_participant_db(db, participant_id="001")
    assert "cat_browser_ratio" in df.columns
    assert "has_app_data" in df.columns
    assert "locked_ratio" in df.columns

def test_load_participant_db_no_features_json_column(tmp_path):
    db = _make_db(tmp_path)
    df = load_participant_db(db, participant_id="001")
    assert "features_json" not in df.columns

def test_load_participant_db_row_count(tmp_path):
    db = _make_db(tmp_path, rows=5)
    df = load_participant_db(db, participant_id="001")
    assert len(df) == 5

def test_load_participant_db_has_window_start_ts(tmp_path):
    db = _make_db(tmp_path)
    df = load_participant_db(db, participant_id="001")
    assert "window_start_ts" in df.columns

def test_load_participant_db_profile_filter(tmp_path):
    db = _make_db(tmp_path, rows=2, profiles=("W60_S30", "W120_S60"))
    df = load_participant_db(db, participant_id="001", profiles=["W60_S30"])
    assert (df["window_profile"] == "W60_S30").all()
    assert len(df) == 2

def test_load_participant_db_all_profiles(tmp_path):
    db = _make_db(tmp_path, rows=2, profiles=("W60_S30", "W120_S60"))
    df = load_participant_db(db, participant_id="001", profiles=["W60_S30", "W120_S60"])
    assert len(df) == 4


# ── load_all_participants ─────────────────────────────────────────────────────

def test_load_all_participants_concatenates(tmp_path):
    p1 = tmp_path / "1" / "participant_P001"
    p2 = tmp_path / "2" / "participant_P002"
    p1.mkdir(parents=True); p2.mkdir(parents=True)
    _make_db(p1, rows=2)
    _make_db(p2, rows=3)
    df = load_all_participants(
        base_dir=str(tmp_path),
        participant_folder_numbers=[1, 2],
        profiles=["W60_S30"],
    )
    assert len(df) == 5
    assert set(df["participant_id"].unique()) == {"001", "002"}

def test_load_all_participants_skips_missing(tmp_path):
    p1 = tmp_path / "1" / "participant_P001"
    p1.mkdir(parents=True)
    _make_db(p1, rows=2)
    # folder 2 does not exist
    df = load_all_participants(
        base_dir=str(tmp_path),
        participant_folder_numbers=[1, 2],
        profiles=["W60_S30"],
    )
    assert len(df) == 2
    assert set(df["participant_id"].unique()) == {"001"}
