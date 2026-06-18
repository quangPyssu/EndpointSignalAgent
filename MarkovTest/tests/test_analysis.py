import pytest
import pandas as pd
import sys, os

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from analysis import (
    state_distribution,
    transition_table,
    sparsity_check,
    chronological_split,
    unseen_transition_rate,
)


def _make_df(participant_sequences: dict[str, list[str]]) -> pd.DataFrame:
    """Build a minimal dataframe from {participant_id: [state, state, ...]}."""
    rows = []
    for pid, states in participant_sequences.items():
        for i, s in enumerate(states):
            rows.append({
                "participant_id": pid,
                "window_start_ts": f"2026-01-01T{i:02d}:00:00Z",
                "markov_state_vA": s,
                "is_abnormal": 0,
            })
    return pd.DataFrame(rows)


# ── state_distribution ────────────────────────────────────────────────────────

def test_state_distribution_columns():
    df = _make_df({"001": ["A", "A", "B"]})
    result = state_distribution(df)
    assert set(result.columns) >= {"participant_id", "markov_state_vA", "count", "pct"}

def test_state_distribution_counts():
    df = _make_df({"001": ["A", "A", "B"]})
    result = state_distribution(df)
    row_a = result[(result["participant_id"] == "001") & (result["markov_state_vA"] == "A")]
    assert row_a["count"].iloc[0] == 2

def test_state_distribution_pct_sums_to_one_per_participant():
    df = _make_df({"001": ["A", "B", "C"], "002": ["A", "A"]})
    result = state_distribution(df)
    for pid in ["001", "002"]:
        total = result[result["participant_id"] == pid]["pct"].sum()
        assert abs(total - 1.0) < 1e-9


# ── transition_table ──────────────────────────────────────────────────────────

def test_transition_table_columns():
    df = _make_df({"001": ["A", "B", "A"]})
    result = transition_table(df)
    assert set(result.columns) >= {"participant_id", "markov_state_vA", "next_state", "transition_count"}

def test_transition_table_counts():
    df = _make_df({"001": ["A", "B", "A", "A"]})
    result = transition_table(df)
    ab = result[(result["participant_id"] == "001") & (result["markov_state_vA"] == "A") & (result["next_state"] == "B")]
    ba = result[(result["participant_id"] == "001") & (result["markov_state_vA"] == "B") & (result["next_state"] == "A")]
    aa = result[(result["participant_id"] == "001") & (result["markov_state_vA"] == "A") & (result["next_state"] == "A")]
    assert ab["transition_count"].iloc[0] == 1
    assert ba["transition_count"].iloc[0] == 1
    assert aa["transition_count"].iloc[0] == 1

def test_transition_table_no_cross_participant_transitions():
    df = _make_df({"001": ["A", "B"], "002": ["C", "D"]})
    result = transition_table(df)
    bad = result[(result["participant_id"] == "001") & (result["next_state"].isin(["C", "D"]))]
    assert len(bad) == 0


# ── sparsity_check ────────────────────────────────────────────────────────────

def test_sparsity_check_columns():
    df = _make_df({"001": ["A", "A", "B"]})
    t = transition_table(df)
    result = sparsity_check(df, t)
    assert set(result.columns) >= {"participant_id", "unique_states", "unique_transitions", "rare_transition_pct"}

def test_sparsity_check_unique_states():
    df = _make_df({"001": ["A", "B", "A", "C"]})
    t = transition_table(df)
    result = sparsity_check(df, t)
    assert result[result["participant_id"] == "001"]["unique_states"].iloc[0] == 3

def test_sparsity_check_rare_transitions_ratio():
    df = _make_df({"001": ["A", "B", "A", "C"]})
    t = transition_table(df)
    result = sparsity_check(df, t, rare_threshold=5)
    assert result[result["participant_id"] == "001"]["rare_transition_pct"].iloc[0] == 1.0


# ── chronological_split ───────────────────────────────────────────────────────

def test_chronological_split_proportions():
    states = ["A"] * 7 + ["B"] * 3
    df = _make_df({"001": states})
    df["is_abnormal"] = 0
    train, val = chronological_split(df, participant_id="001", train_ratio=0.7)
    assert len(train) == 7
    assert len(val) == 3

def test_chronological_split_excludes_abnormal_from_train():
    df = _make_df({"001": ["A"] * 10})
    df.loc[0:2, "is_abnormal"] = 1
    train, val = chronological_split(df, participant_id="001", train_ratio=0.7)
    assert (train["is_abnormal"] == 0).all()

def test_chronological_split_order_preserved():
    states = [f"S{i}" for i in range(10)]
    df = _make_df({"001": states})
    df["is_abnormal"] = 0
    train, val = chronological_split(df, participant_id="001", train_ratio=0.7)
    assert list(train["markov_state_vA"]) == states[:7]
    assert list(val["markov_state_vA"]) == states[7:]


# ── unseen_transition_rate ────────────────────────────────────────────────────

def test_unseen_transition_rate_zero_when_all_seen():
    train = _make_df({"001": ["A", "B", "A", "B"]})
    val = _make_df({"001": ["A", "B", "A"]})
    train_t = transition_table(train)
    val_t = transition_table(val)
    rate = unseen_transition_rate(train_t, val_t, participant_id="001")
    assert rate == 0.0

def test_unseen_transition_rate_all_unseen():
    train = _make_df({"001": ["A", "B"]})
    val = _make_df({"001": ["C", "D"]})
    train_t = transition_table(train)
    val_t = transition_table(val)
    rate = unseen_transition_rate(train_t, val_t, participant_id="001")
    assert rate == 1.0

def test_unseen_transition_rate_partial():
    # train: A→B only. val: A→B (seen) + A→C (unseen). Rate = 1/2 transitions by count
    train = _make_df({"001": ["A", "B"]})
    val_rows = [
        {"participant_id": "001", "window_start_ts": "2026-01-01T00:00:00Z", "markov_state_vA": "A", "is_abnormal": 0},
        {"participant_id": "001", "window_start_ts": "2026-01-01T01:00:00Z", "markov_state_vA": "B", "is_abnormal": 0},
        {"participant_id": "001", "window_start_ts": "2026-01-01T02:00:00Z", "markov_state_vA": "A", "is_abnormal": 0},
        {"participant_id": "001", "window_start_ts": "2026-01-01T03:00:00Z", "markov_state_vA": "C", "is_abnormal": 0},
    ]
    val = pd.DataFrame(val_rows)
    train_t = transition_table(train)
    val_t = transition_table(val)
    rate = unseen_transition_rate(train_t, val_t, participant_id="001")
    assert abs(rate - 0.5) < 1e-9
