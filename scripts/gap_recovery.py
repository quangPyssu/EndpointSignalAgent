import argparse
import sqlite3
import sys
from datetime import datetime
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
        if not _column_exists(conn, "has_collection_gap"):
            conn.execute(sql.ADD_COLLECTION_GAP_COL)
            conn.execute(sql.CREATE_GAP_INDEX)

        if not _column_exists(conn, "in_warm_up"):
            conn.execute(sql.ADD_IN_WARM_UP_COL)
            conn.execute(sql.CREATE_WARM_INDEX)

        conn.execute(sql.DROP_GAPS_TABLE)
        conn.execute(sql.BUILD_GAPS_TABLE)

        gaps_found = _count_gaps(conn)

        conn.execute("UPDATE feature_rows SET has_collection_gap = 0")
        conn.execute(sql.FLAG_DRAIN_ROWS)
        drain_flagged = conn.execute(
            "SELECT COUNT(*) FROM feature_rows WHERE has_collection_gap = 1"
        ).fetchone()[0]

        conn.execute("UPDATE feature_rows SET in_warm_up = 0")
        conn.execute(sql.FLAG_WARMUP_ROWS)
        warmup_flagged = conn.execute(
            "SELECT COUNT(*) FROM feature_rows WHERE in_warm_up = 1"
        ).fetchone()[0]

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


def snapshot_before(db_path: Path) -> dict:
    conn = sqlite3.connect(str(db_path))
    try:
        row = conn.execute(sql.BEFORE_SNAPSHOT_QUERY).fetchone()
        conn.execute(sql.DROP_GAPS_TABLE)
        conn.execute(sql.BUILD_GAPS_TABLE)
        gaps = conn.execute("SELECT COUNT(*) FROM _gaps").fetchone()[0]
        conn.execute(sql.DROP_GAPS_TABLE)
    finally:
        conn.close()
    return {
        "total_rows":   row[0],
        "naive_usable": row[1],
        "distinct_ts":  row[2],
        "date_start":   row[3],
        "date_end":     row[4],
        "gaps_found":   gaps,
    }


def write_report(records: list, report_path: Path) -> None:
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

    total_rows_sum = drain_sum = warmup_sum = clean_sum = 0

    for rec in records:
        db_label = rec["db_path"].parent.name
        before   = rec["before"]
        after    = rec["after"]
        total    = before["total_rows"]
        gaps     = after["gaps_found"]
        drain    = after["drain_rows_flagged"]
        warmup   = after["warmup_rows_flagged"]
        clean    = after["clean_rows"]
        loss_pct = round(100.0 * (drain + warmup) / total, 1) if total else 0
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
        "- **Data loss %** = (drain + warm-up rows) / total rows — excluded from training.",
        "- **Clean rows** = `has_collection_gap=0 AND in_warm_up=0 AND has_system_data=1`",
        "",
        "## Before vs After (naive usable → clean)",
        "",
        "| DB | Naive usable (before) | Clean rows (after) | Δ rows | Δ % |",
        "|----|----------------------|-------------------|--------|-----|",
    ]

    for rec in records:
        db_label  = rec["db_path"].parent.name
        naive     = rec["before"]["naive_usable"]
        clean     = rec["after"]["clean_rows"]
        delta     = clean - naive
        delta_pct = round(100.0 * delta / naive, 1) if naive else 0
        sign      = "+" if delta >= 0 else ""
        lines.append(f"| {db_label} | {naive:,} | {clean:,} | {sign}{delta:,} | {sign}{delta_pct}% |")

    lines += ["", f"*Generated by `scripts/gap_recovery.py` at {run_ts}*", ""]
    report_path.write_text("\n".join(lines))
    print("\n".join(lines))
    print(f"\nReport saved to {report_path}")


def main():
    parser = argparse.ArgumentParser(description="Retroactive gap recovery for features.db files")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--db",  metavar="PATH", help="Single features.db to migrate")
    group.add_argument("--all", metavar="DIR",  help="Base directory with numbered DB folders")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print gap summary without modifying any DB")
    args = parser.parse_args()

    targets = [Path(args.db)] if args.db else _find_dbs(Path(args.all))

    if not targets:
        print("No features.db files found.", file=sys.stderr)
        sys.exit(1)

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


if __name__ == "__main__":
    main()
