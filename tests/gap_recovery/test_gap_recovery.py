import sqlite3
import pytest
from pathlib import Path
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from scripts.gap_recovery import migrate_db


def query(db_path: Path, sql: str, params=()):
    conn = sqlite3.connect(str(db_path))
    result = conn.execute(sql, params).fetchall()
    conn.close()
    return result


def column_names(db_path: Path):
    rows = query(db_path, "PRAGMA table_info(feature_rows)")
    return [r[1] for r in rows]


def test_adds_has_collection_gap_column(db_path):
    migrate_db(db_path)
    assert "has_collection_gap" in column_names(db_path)


def test_adds_in_warm_up_column(db_path):
    migrate_db(db_path)
    assert "in_warm_up" in column_names(db_path)


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
    # 3 warmup rows × 3 profiles = 9 (all within 120s of resume_ts)
    assert flagged == 9


def test_post_warmup_rows_are_not_flagged(db_path):
    migrate_db(db_path)
    wrong = query(
        db_path,
        """SELECT COUNT(*) FROM feature_rows
           WHERE in_warm_up = 1
             AND window_start_ts >= '2026-01-01T08:13:01.0000000+00:00'"""
    )[0][0]
    assert wrong == 0


def test_idempotent_double_run(db_path):
    migrate_db(db_path)
    migrate_db(db_path)
    drain = query(db_path, "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap = 1")[0][0]
    warmup = query(db_path, "SELECT COUNT(*) FROM feature_rows WHERE in_warm_up = 1")[0][0]
    assert drain == 9
    assert warmup == 9


def test_migrate_returns_stats(db_path):
    result = migrate_db(db_path)
    assert result["gaps_found"] == 1
    assert result["drain_rows_flagged"] == 9
    assert result["warmup_rows_flagged"] == 9
    assert result["total_rows"] > 0
    assert result["clean_rows"] > 0


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
    conn = sqlite3.connect(str(db_path))
    cols = [r[1] for r in conn.execute("PRAGMA table_info(feature_rows)").fetchall()]
    conn.close()
    assert "has_collection_gap" not in cols


from scripts.gap_recovery import snapshot_before, write_report


def test_snapshot_before_returns_counts(db_path):
    snap = snapshot_before(db_path)
    assert snap["total_rows"] > 0
    assert snap["naive_usable"] > 0
    assert snap["naive_usable"] < snap["total_rows"]  # drain rows have sys=0
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
    assert str(after["drain_rows_flagged"]) in content
    assert str(after["warmup_rows_flagged"]) in content


def test_write_report_delta_direction(db_path, tmp_path):
    before = snapshot_before(db_path)
    after  = migrate_db(db_path)
    report_path = tmp_path / "report.md"
    write_report(
        [{"db_path": db_path, "before": before, "after": after}],
        report_path,
    )
    assert after["clean_rows"] <= before["naive_usable"]
