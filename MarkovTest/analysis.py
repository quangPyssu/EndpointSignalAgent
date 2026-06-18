import pandas as pd


def state_distribution(df: pd.DataFrame) -> pd.DataFrame:
    counts = (
        df.groupby(["participant_id", "markov_state_vA"])
        .size()
        .reset_index(name="count")
    )
    counts["pct"] = (
        counts["count"]
        / counts.groupby("participant_id")["count"].transform("sum")
    )
    return counts.sort_values(["participant_id", "count"], ascending=[True, False])


def transition_table(df: pd.DataFrame) -> pd.DataFrame:
    df = df.sort_values(["participant_id", "window_start_ts"]).copy()
    df["next_state"] = df.groupby("participant_id")["markov_state_vA"].shift(-1)
    transitions = (
        df.dropna(subset=["next_state"])
        .groupby(["participant_id", "markov_state_vA", "next_state"])
        .size()
        .reset_index(name="transition_count")
    )
    return transitions


def sparsity_check(
    df: pd.DataFrame,
    transitions: pd.DataFrame,
    rare_threshold: int = 5,
) -> pd.DataFrame:
    unique_states = (
        df.groupby("participant_id")["markov_state_vA"]
        .nunique()
        .reset_index(name="unique_states")
    )
    unique_trans = (
        transitions.groupby("participant_id")
        .size()
        .reset_index(name="unique_transitions")
    )
    rare = (
        transitions[transitions["transition_count"] < rare_threshold]
        .groupby("participant_id")
        .size()
        .reset_index(name="rare_transitions")
    )
    result = unique_states.merge(unique_trans, on="participant_id", how="left")
    result = result.merge(rare, on="participant_id", how="left")
    result["rare_transitions"] = result["rare_transitions"].fillna(0).astype(int)
    result["rare_transition_pct"] = result["rare_transitions"] / result["unique_transitions"].replace(0, 1)
    return result


def chronological_split(
    df: pd.DataFrame,
    participant_id: str,
    train_ratio: float = 0.70,
) -> tuple[pd.DataFrame, pd.DataFrame]:
    sub = df[df["participant_id"] == participant_id].copy()
    sub = sub.sort_values("window_start_ts")
    normal = sub[sub["is_abnormal"] == 0].reset_index(drop=True)
    n_train = int(len(normal) * train_ratio)
    return normal.iloc[:n_train], normal.iloc[n_train:]


def unseen_transition_rate(
    train_transitions: pd.DataFrame,
    val_transitions: pd.DataFrame,
    participant_id: str,
) -> float:
    train_t = train_transitions[train_transitions["participant_id"] == participant_id]
    val_t = val_transitions[val_transitions["participant_id"] == participant_id]

    if val_t.empty:
        return 0.0

    train_from_states = set(train_t["markov_state_vA"])
    val_t_known = val_t[val_t["markov_state_vA"].isin(train_from_states)]
    if val_t_known.empty:
        return 1.0

    train_pairs_df = (
        train_t[["markov_state_vA", "next_state"]]
        .drop_duplicates()
        .assign(seen=True)
    )
    val_marked = val_t_known.merge(train_pairs_df, on=["markov_state_vA", "next_state"], how="left")
    total = int(val_marked["transition_count"].sum())
    unseen = int(val_marked[val_marked["seen"].isna()]["transition_count"].sum())
    return unseen / total if total > 0 else 0.0
