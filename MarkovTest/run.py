"""
Usage:
    python run.py --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
    python run.py --version vE --profile W60_S30 --base-dir /path/to/DataBase
    python run.py --version vF --profile W120_S60 --participants 1 2 3 --base-dir /path
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


def _load_new_versions():
    """Import new versions lazily so run.py works even if states_vW/vE/vF don't exist yet."""
    try:
        from states_vW import assign_markov_state_vW
        _VERSION_FN["vW"] = assign_markov_state_vW
    except ImportError:
        pass
    try:
        from states_vE import assign_markov_state_vE
        _VERSION_FN["vE"] = assign_markov_state_vE
    except ImportError:
        pass
    try:
        from states_vF import assign_markov_state_vF
        _VERSION_FN["vF"] = assign_markov_state_vF
    except ImportError:
        pass


def main():
    _load_new_versions()

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
    parser.add_argument(
        "--base-dir", default=None,
        dest="base_dir",
        help="Path to DataBase folder (default: E:/DataBase). "
             "Mac: /Users/lap15174/EndpointSignalAgent/DataBase",
    )
    args = parser.parse_args()

    out_path = args.out or f"results/report_{args.version}_{args.profile}.csv"
    participant_numbers = args.participants or list(range(1, 16))

    load_kwargs = dict(
        participant_folder_numbers=participant_numbers,
        profiles=[args.profile],
    )
    if args.base_dir:
        load_kwargs["base_dir"] = args.base_dir

    print(f"Loading version={args.version} profile={args.profile} "
          f"for {len(participant_numbers)} participant(s)...")
    df = load_all_participants(**load_kwargs)

    if df.empty:
        print("ERROR: No data loaded. Check --base-dir path.", file=sys.stderr)
        sys.exit(1)

    state_col = f"markov_state_{args.version}"
    assign_fn = _VERSION_FN[args.version]

    print(f"Loaded {len(df):,} rows ({df['is_abnormal'].sum():,} abnormal tagged). "
          f"Assigning {state_col}...")
    df[state_col] = df.apply(assign_fn, axis=1)

    print(f"\nTop states overall:\n{df[state_col].value_counts().head(15).to_string()}\n")

    analysis_df = df.rename(columns={state_col: "markov_state_vA"})

    participant_ids = sorted(analysis_df["participant_id"].unique())
    reports = [build_participant_report(analysis_df, pid) for pid in participant_ids]

    print(f"\n=== Per-participant summary ({args.version} / {args.profile}) ===")
    print_summary(reports)

    Path(out_path).parent.mkdir(parents=True, exist_ok=True)
    save_reports_csv(reports, out_path)


if __name__ == "__main__":
    main()
