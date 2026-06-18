import pandas as pd
from analysis import (
    state_distribution,
    transition_table,
    sparsity_check,
    chronological_split,
    unseen_transition_rate,
)


def build_participant_report(
    df: pd.DataFrame,
    participant_id: str,
) -> dict:
    sub = df[df["participant_id"] == participant_id]
    if sub.empty:
        return {"participant_id": participant_id, "error": "no data"}

    dist = state_distribution(sub)
    p_dist = dist[dist["participant_id"] == participant_id].copy()
    top5 = p_dist.nlargest(5, "count")[["markov_state_vA", "pct"]].to_dict("records")

    trans = transition_table(sub)
    p_trans = trans[trans["participant_id"] == participant_id]
    top10_trans = (
        p_trans.nlargest(10, "transition_count")[
            ["markov_state_vA", "next_state", "transition_count"]
        ].to_dict("records")
    )

    sparsity = sparsity_check(sub, p_trans)
    p_sparsity = sparsity[sparsity["participant_id"] == participant_id]
    unique_states = int(p_sparsity["unique_states"].iloc[0]) if len(p_sparsity) else 0
    unique_transitions = int(p_sparsity["unique_transitions"].iloc[0]) if len(p_sparsity) else 0
    rare_pct = float(p_sparsity["rare_transition_pct"].iloc[0]) if len(p_sparsity) else 0.0

    train, val = chronological_split(sub, participant_id)
    abnormal = sub[sub["is_abnormal"] == 1]
    train_t = transition_table(train)

    normal_unseen = 0.0
    if len(val) > 1:
        val_t = transition_table(val)
        normal_unseen = unseen_transition_rate(train_t, val_t, participant_id)

    abnormal_unseen = 0.0
    if len(abnormal) > 1:
        abn_t = transition_table(abnormal)
        abnormal_unseen = unseen_transition_rate(train_t, abn_t, participant_id)

    return {
        "participant_id": participant_id,
        "number_of_windows": len(sub),
        "number_of_unique_states": unique_states,
        "top_5_states": top5,
        "number_of_unique_transitions": unique_transitions,
        "rare_transition_pct": round(rare_pct, 4),
        "top_10_transitions": top10_trans,
        "normal_val_unseen_rate": round(normal_unseen, 4),
        "abnormal_unseen_rate": round(abnormal_unseen, 4),
    }


def print_summary(reports: list[dict]) -> None:
    header = (
        f"{'PID':<6} {'Windows':>8} {'States':>7} {'Transitions':>12} "
        f"{'RarePct':>8} {'NormalUnseen':>13} {'AbnUnseen':>10}"
    )
    print(header)
    print("-" * len(header))
    for r in reports:
        if "error" in r:
            print(f"{r['participant_id']:<6}  ERROR: {r['error']}")
            continue
        print(
            f"{r['participant_id']:<6} "
            f"{r['number_of_windows']:>8} "
            f"{r['number_of_unique_states']:>7} "
            f"{r['number_of_unique_transitions']:>12} "
            f"{r['rare_transition_pct']:>8.1%} "
            f"{r['normal_val_unseen_rate']:>13.1%} "
            f"{r['abnormal_unseen_rate']:>10.1%}"
        )


def save_reports_csv(reports: list[dict], path: str) -> None:
    flat = []
    for r in reports:
        flat.append({
            "participant_id": r.get("participant_id"),
            "number_of_windows": r.get("number_of_windows"),
            "number_of_unique_states": r.get("number_of_unique_states"),
            "number_of_unique_transitions": r.get("number_of_unique_transitions"),
            "rare_transition_pct": r.get("rare_transition_pct"),
            "normal_val_unseen_rate": r.get("normal_val_unseen_rate"),
            "abnormal_unseen_rate": r.get("abnormal_unseen_rate"),
        })
    pd.DataFrame(flat).to_csv(path, index=False)
    print(f"Report saved to {path}")
