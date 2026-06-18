"""
Usage:
    python run.py                          # W60_S30, all 15 participants
    python run.py --profile W120_S60
    python run.py --profile W30_S15
    python run.py --profile W60_S30 --participants 1 2 3
    python run.py --profile W60_S30 --out results/report_W60_S30.csv
"""
import argparse
import sys
from pathlib import Path

from loader import load_all_participants, DEFAULT_PROFILES
from states import assign_markov_state_vA
from report import build_participant_report, print_summary, save_reports_csv


def main():
    parser = argparse.ArgumentParser(description="Markov State Version A — real-data analysis")
    parser.add_argument("--profile", choices=DEFAULT_PROFILES, default="W60_S30")
    parser.add_argument(
        "--participants", nargs="*", type=int, default=None,
        help="Folder numbers to include (e.g. 1 2 3). Default: all 1-15."
    )
    parser.add_argument("--out", default="markov_report_vA.csv", help="Output CSV path")
    args = parser.parse_args()

    participant_numbers = args.participants if args.participants else list(range(1, 16))

    print(f"Loading profile={args.profile} for {len(participant_numbers)} participant(s)...")
    df = load_all_participants(
        participant_folder_numbers=participant_numbers,
        profiles=[args.profile],
    )

    if df.empty:
        print("ERROR: No data loaded. Check E:/DataBase paths.", file=sys.stderr)
        sys.exit(1)

    print(f"Loaded {len(df):,} rows ({df['is_abnormal'].sum():,} abnormal tagged). Assigning markov_state_vA...")
    df["markov_state_vA"] = df.apply(assign_markov_state_vA, axis=1)

    print(f"\nTop states overall:\n{df['markov_state_vA'].value_counts().head(10).to_string()}\n")

    participant_ids = sorted(df["participant_id"].unique())
    reports = [build_participant_report(df, pid) for pid in participant_ids]

    print("\n=== Per-participant summary ===")
    print_summary(reports)

    out_path = args.out
    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    save_reports_csv(reports, out_path)


if __name__ == "__main__":
    main()
