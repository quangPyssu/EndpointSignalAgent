"""
Usage:
    python run.py                                      # vA, W60_S30, all 15 participants
    python run.py --version vB --profile W60_S30
    python run.py --version vC --out results/report_vC_W60_S30.csv
    python run.py --version vD --participants 1 2 3
"""
import argparse
import sys
from pathlib import Path

from loader import load_all_participants, DEFAULT_PROFILES
from states import assign_markov_state_vA
from states_vB import assign_markov_state_vB
from states_vC import assign_markov_state_vC
from states_vD import assign_markov_state_vD
from report import build_participant_report, print_summary, save_reports_csv

_VERSION_FN = {
    "vA": assign_markov_state_vA,
    "vB": assign_markov_state_vB,
    "vC": assign_markov_state_vC,
    "vD": assign_markov_state_vD,
}


def main():
    parser = argparse.ArgumentParser(description="Markov State evaluation — multi-version")
    parser.add_argument("--version", choices=list(_VERSION_FN), default="vA",
                        help="State schema version (default: vA)")
    parser.add_argument("--profile", choices=DEFAULT_PROFILES, default="W60_S30")
    parser.add_argument(
        "--participants", nargs="*", type=int, default=None,
        help="Folder numbers to include (e.g. 1 2 3). Default: all 1-15.",
    )
    parser.add_argument(
        "--out", default=None,
        help="Output CSV path. Default: results/report_{VERSION}_{PROFILE}.csv",
    )
    args = parser.parse_args()

    out_path = args.out or f"results/report_{args.version}_{args.profile}.csv"
    participant_numbers = args.participants or list(range(1, 16))

    print(f"Loading version={args.version} profile={args.profile} "
          f"for {len(participant_numbers)} participant(s)...")
    df = load_all_participants(
        participant_folder_numbers=participant_numbers,
        profiles=[args.profile],
    )

    if df.empty:
        print("ERROR: No data loaded. Check E:/DataBase paths.", file=sys.stderr)
        sys.exit(1)

    state_col = f"markov_state_{args.version}"
    assign_fn = _VERSION_FN[args.version]

    print(f"Loaded {len(df):,} rows ({df['is_abnormal'].sum():,} abnormal tagged). "
          f"Assigning {state_col}...")
    df[state_col] = df.apply(assign_fn, axis=1)

    print(f"\nTop states overall:\n{df[state_col].value_counts().head(10).to_string()}\n")

    # analysis.py expects "markov_state_vA" — rename for compatibility
    analysis_df = df.rename(columns={state_col: "markov_state_vA"})

    participant_ids = sorted(analysis_df["participant_id"].unique())
    reports = [build_participant_report(analysis_df, pid) for pid in participant_ids]

    print(f"\n=== Per-participant summary ({args.version} / {args.profile}) ===")
    print_summary(reports)

    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    save_reports_csv(reports, out_path)


if __name__ == "__main__":
    main()
