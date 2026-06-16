# Markov State Version A Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Assign `markov_state_vA` (WorkMode × EngagementMode) to every feature-window row for all 15 participants and produce a per-participant report that lets us accept or reject Version A as the Markov fallback.

**Architecture:** Pure-function state assignment (`states.py`) → CSV loader with column normalization (`loader.py`) → analysis pipeline for distribution, transitions, sparsity, and train/val unseen-rate checks (`analysis.py`) → report writer (`report.py`) → CLI entrypoint (`run.py`). All functions are unit-tested with synthetic dataframes before touching real data.

**Tech Stack:** Python 3.11+, pandas, pytest. No ML libraries needed.

---

## Data layout (read this before touching any file)

Real CSVs live at:

```
E:/DataBase/{N}/features_all_{PROFILE}_{TIMESTAMP}.csv
    where N = 1..15, PROFILE ∈ {W60_S30, W120_S60, W30_S15}
```

Participant 4's subfolder is named `participant_N004` (not `P004`). Participant 14's sub-folder has a timestamp suffix. The top-level folder CSVs (directly under `E:/DataBase/{N}/`) are used by the loader — not the sub-folder ones.

CSV column name quirks:
- `WindowStartTs` (PascalCase) → loader normalises to `window_start_ts`
- All feature columns are already snake_case (`has_app_data`, `cat_browser_ratio`, etc.)
- No `participant_id` column in the CSV — loader derives it from folder number N
- No `window_profile` column — loader derives it from filename (`W60_S30`, etc.)

---

## File structure

```
markovTest/
  requirements.txt        — pandas, pytest
  states.py               — assign_work_mode, assign_engagement_mode, assign_markov_state_vA
  loader.py               — load_participant_csv, load_all_participants
  analysis.py             — state_distribution, transition_table, sparsity_check,
                            chronological_split, unseen_transition_rate
  report.py               — build_participant_report, save_reports_csv, print_summary
  run.py                  — CLI: load → assign → analyse → report
  tests/
    __init__.py
    test_states.py         — unit tests for all state assignment functions
    test_analysis.py       — unit tests for all analysis functions
```

All paths in this plan are relative to `markovTest/` unless stated otherwise.

---

## Task 1: Scaffold

**Files:**
- Create: `requirements.txt`
- Create: `tests/__init__.py`

- [ ] **Step 1: Create requirements.txt**

```
pandas>=2.0
pytest>=8.0
```

- [ ] **Step 2: Create tests/__init__.py**

Empty file — just makes `tests/` a package.

```python
```

- [ ] **Step 3: Install dependencies**

Run from `markovTest/`:
```
pip install -r requirements.txt
```

Expected: both packages install without error.

- [ ] **Step 4: Verify pytest can discover tests**

```
pytest tests/ --collect-only
```

Expected: `no tests ran` (no tests exist yet) — but no import errors.

- [ ] **Step 5: Commit**

```bash
git add markovTest/requirements.txt markovTest/tests/__init__.py
git commit -m "chore: scaffold markovTest project"
```

---

## Task 2: State assignment functions

**Files:**
- Create: `states.py`
- Create: `tests/test_states.py`

### Step 1: Write failing tests

- [ ] Create `tests/test_states.py`:

```python
import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states import assign_work_mode, assign_engagement_mode, assign_markov_state_vA


# ── WorkMode ─────────────────────────────────────────────────────────────────

def _row(**kwargs):
    """Build a minimal feature row with safe zero defaults."""
    defaults = dict(
        has_app_data=1,
        cat_browser_ratio=0, cat_ide_ratio=0, cat_terminal_ratio=0,
        cat_comms_ratio=0, cat_office_ratio=0, cat_media_ratio=0,
        cat_gaming_ratio=0, cat_remoteaccess_ratio=0,
        cat_filemanager_ratio=0, cat_system_ratio=0, cat_other_ratio=0,
        locked_ratio=0, active_work_ratio=0,
        idle_bucket_mean_sec=0, idle_ge_300_ratio=0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


def test_work_mode_no_app():
    assert assign_work_mode(_row(has_app_data=0)) == "NoApp"


def test_work_mode_remote_access_priority():
    # remoteaccess >= 0.30 beats everything including high ide+terminal
    r = _row(cat_remoteaccess_ratio=0.35, cat_ide_ratio=0.40, cat_terminal_ratio=0.20)
    assert assign_work_mode(r) == "RemoteAccessWork"


def test_work_mode_developer_ide_terminal_combined():
    r = _row(cat_ide_ratio=0.30, cat_terminal_ratio=0.25)  # sum = 0.55
    assert assign_work_mode(r) == "DeveloperWork"


def test_work_mode_terminal_only():
    r = _row(cat_terminal_ratio=0.45, cat_ide_ratio=0.05)  # dev_ratio=0.50 exactly → DeveloperWork
    assert assign_work_mode(r) == "DeveloperWork"

def test_work_mode_terminal_alone():
    r = _row(cat_terminal_ratio=0.42, cat_ide_ratio=0.02)  # dev_ratio=0.44 < 0.50 → TerminalWork
    assert assign_work_mode(r) == "TerminalWork"


def test_work_mode_dominant_browser():
    r = _row(cat_browser_ratio=0.65)
    assert assign_work_mode(r) == "BrowserWork"


def test_work_mode_dominant_comms():
    r = _row(cat_comms_ratio=0.55)
    assert assign_work_mode(r) == "CommsWork"


def test_work_mode_dominant_office():
    r = _row(cat_office_ratio=0.60)
    assert assign_work_mode(r) == "OfficeWork"


def test_work_mode_dominant_media():
    r = _row(cat_media_ratio=0.70)
    assert assign_work_mode(r) == "MediaWork"


def test_work_mode_dominant_gaming():
    r = _row(cat_gaming_ratio=0.55)
    assert assign_work_mode(r) == "GamingWork"


def test_work_mode_dominant_file():
    r = _row(cat_filemanager_ratio=0.52)
    assert assign_work_mode(r) == "FileWork"


def test_work_mode_dominant_system():
    r = _row(cat_system_ratio=0.51)
    assert assign_work_mode(r) == "SystemWork"


def test_work_mode_dominant_other():
    r = _row(cat_other_ratio=0.55)
    assert assign_work_mode(r) == "OtherWork"


def test_work_mode_mixed_no_dominant():
    # all ratios below 0.50, no special priority hit
    r = _row(cat_browser_ratio=0.30, cat_comms_ratio=0.30, cat_office_ratio=0.20)
    assert assign_work_mode(r) == "MixedWork"


def test_work_mode_exact_thresholds_remote_access():
    # exactly 0.30 → RemoteAccessWork
    r = _row(cat_remoteaccess_ratio=0.30)
    assert assign_work_mode(r) == "RemoteAccessWork"


def test_work_mode_remote_access_below_threshold():
    # 0.29 → falls through to dominant-category check
    r = _row(cat_remoteaccess_ratio=0.29, cat_browser_ratio=0.55)
    assert assign_work_mode(r) == "BrowserWork"


# ── EngagementMode ────────────────────────────────────────────────────────────

def test_engagement_locked():
    r = _row(locked_ratio=0.80)
    assert assign_engagement_mode(r) == "Locked"


def test_engagement_locked_exact_threshold():
    r = _row(locked_ratio=0.80)
    assert assign_engagement_mode(r) == "Locked"


def test_engagement_locked_below_threshold():
    r = _row(locked_ratio=0.79, has_idle_data=1, idle_ge_300_ratio=0, idle_bucket_mean_sec=10, active_work_ratio=0.6)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_no_idle_data_active():
    r = _row(has_idle_data=0, active_work_ratio=0.75, locked_ratio=0)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_no_idle_data_unknown():
    r = _row(has_idle_data=0, active_work_ratio=0.50, locked_ratio=0)
    assert assign_engagement_mode(r) == "UnknownEngagement"


def test_engagement_long_idle():
    r = _row(idle_ge_300_ratio=0.50, idle_bucket_mean_sec=400, locked_ratio=0)
    assert assign_engagement_mode(r) == "LongIdle"


def test_engagement_light_idle_mean():
    r = _row(idle_ge_300_ratio=0.10, idle_bucket_mean_sec=90, locked_ratio=0)
    assert assign_engagement_mode(r) == "LightIdle"


def test_engagement_active_via_active_work_ratio():
    r = _row(idle_ge_300_ratio=0.05, idle_bucket_mean_sec=30, active_work_ratio=0.55, locked_ratio=0)
    assert assign_engagement_mode(r) == "Active"


def test_engagement_light_idle_fallback():
    # idle data present, not long-idle, mean < 60, active_work_ratio < 0.50
    r = _row(idle_ge_300_ratio=0.05, idle_bucket_mean_sec=30, active_work_ratio=0.40, locked_ratio=0)
    assert assign_engagement_mode(r) == "LightIdle"


# ── assign_markov_state_vA ────────────────────────────────────────────────────

def test_state_locked_collapses():
    r = _row(locked_ratio=0.90)
    assert assign_markov_state_vA(r) == "Locked"


def test_state_noapp_light_idle():
    r = _row(has_app_data=0, has_idle_data=1, idle_ge_300_ratio=0, idle_bucket_mean_sec=90, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_LightIdle"


def test_state_noapp_long_idle():
    r = _row(has_app_data=0, has_idle_data=1, idle_ge_300_ratio=0.60, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_LongIdle"


def test_state_browser_active():
    r = _row(cat_browser_ratio=0.70, active_work_ratio=0.60,
             idle_bucket_mean_sec=20, idle_ge_300_ratio=0, locked_ratio=0)
    assert assign_markov_state_vA(r) == "BrowserWork_Active"


def test_state_developer_light_idle():
    r = _row(cat_ide_ratio=0.35, cat_terminal_ratio=0.25,
             idle_bucket_mean_sec=120, idle_ge_300_ratio=0.10,
             active_work_ratio=0.30, locked_ratio=0)
    assert assign_markov_state_vA(r) == "DeveloperWork_LightIdle"


def test_state_unknown_engagement():
    r = _row(has_app_data=0, has_idle_data=0, active_work_ratio=0.40, locked_ratio=0)
    assert assign_markov_state_vA(r) == "NoApp_UnknownEngagement"
```

- [ ] **Step 2: Run tests — verify they all fail**

```
pytest tests/test_states.py -v
```

Expected: `ImportError: No module named 'states'` (file doesn't exist yet).

- [ ] **Step 3: Create states.py**

```python
def assign_work_mode(row):
    if row.get("has_app_data", 0) == 0:
        return "NoApp"

    dev_ratio = row.get("cat_ide_ratio", 0) + row.get("cat_terminal_ratio", 0)

    if row.get("cat_remoteaccess_ratio", 0) >= 0.30:
        return "RemoteAccessWork"

    if dev_ratio >= 0.50:
        return "DeveloperWork"

    if row.get("cat_terminal_ratio", 0) >= 0.40:
        return "TerminalWork"

    main_ratios = {
        "BrowserWork": row.get("cat_browser_ratio", 0),
        "CommsWork": row.get("cat_comms_ratio", 0),
        "OfficeWork": row.get("cat_office_ratio", 0),
        "MediaWork": row.get("cat_media_ratio", 0),
        "GamingWork": row.get("cat_gaming_ratio", 0),
        "FileWork": row.get("cat_filemanager_ratio", 0),
        "SystemWork": row.get("cat_system_ratio", 0),
        "OtherWork": row.get("cat_other_ratio", 0),
    }

    best_mode = max(main_ratios, key=main_ratios.get)
    best_ratio = main_ratios[best_mode]

    if best_ratio >= 0.50:
        return best_mode

    return "MixedWork"


def assign_engagement_mode(row):
    if row.get("locked_ratio", 0) >= 0.80:
        return "Locked"

    if row.get("has_idle_data", 0) == 0:
        if row.get("active_work_ratio", 0) >= 0.70:
            return "Active"
        return "UnknownEngagement"

    if row.get("idle_ge_300_ratio", 0) >= 0.50:
        return "LongIdle"

    if row.get("idle_bucket_mean_sec", 0) >= 60:
        return "LightIdle"

    if row.get("active_work_ratio", 0) >= 0.50:
        return "Active"

    return "LightIdle"


def assign_markov_state_vA(row):
    work = assign_work_mode(row)
    engage = assign_engagement_mode(row)

    if engage == "Locked":
        return "Locked"

    if work == "NoApp" and engage in ("LongIdle", "LightIdle", "UnknownEngagement"):
        return f"NoApp_{engage}"

    return f"{work}_{engage}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
pytest tests/test_states.py -v
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add markovTest/states.py markovTest/tests/test_states.py
git commit -m "feat(markov): add Version A state assignment functions with tests"
```

---

## Task 3: Data loader

**Files:**
- Create: `loader.py`
- Create: `tests/test_loader.py`

### Context

- CSVs live at `E:/DataBase/{N}/features_all_{PROFILE}_{TIMESTAMP}.csv` (N = 1–15)
- `WindowStartTs` (PascalCase) must be renamed to `window_start_ts`
- `participant_id` = folder number N (string, zero-padded to 3 digits: "001"–"015")
- `window_profile` = profile token extracted from filename (`W60_S30`, `W120_S60`, `W30_S15`)
- When multiple CSVs exist for one participant+profile, pick the one with the latest timestamp (alphabetically last filename works since format is `YYYYMMDD_HHMMSS`)

- [ ] **Step 1: Write failing tests**

Create `tests/test_loader.py`:

```python
import pytest
import pandas as pd
import sys, os, tempfile, pathlib

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from loader import _extract_profile, _normalize_columns, load_participant_csv, load_all_participants


# ── helpers ───────────────────────────────────────────────────────────────────

def test_extract_profile_w60():
    assert _extract_profile("features_all_W60_S30_20260519_210039.csv") == "W60_S30"

def test_extract_profile_w120():
    assert _extract_profile("features_all_W120_S60_20260613_162312.csv") == "W120_S60"

def test_extract_profile_w30():
    assert _extract_profile("features_all_W30_S15_20260607_121507.csv") == "W30_S15"

def test_extract_profile_no_match_raises():
    with pytest.raises(ValueError):
        _extract_profile("some_other_file.csv")


# ── column normalisation ──────────────────────────────────────────────────────

def test_normalize_columns_renames_window_start_ts():
    df = pd.DataFrame({"WindowStartTs": ["2026-01-01"], "has_app_data": [1]})
    result = _normalize_columns(df)
    assert "window_start_ts" in result.columns
    assert "WindowStartTs" not in result.columns

def test_normalize_columns_leaves_snake_case_untouched():
    df = pd.DataFrame({"has_app_data": [1], "cat_browser_ratio": [0.5]})
    result = _normalize_columns(df)
    assert list(result.columns) == ["has_app_data", "cat_browser_ratio"]


# ── load_participant_csv ──────────────────────────────────────────────────────

def _make_csv(tmp_path, filename, rows=2):
    """Write a minimal valid CSV and return its path."""
    data = {
        "WindowStartTs": [f"2026-01-0{i+1}T00:00:00Z" for i in range(rows)],
        "has_app_data": [1] * rows,
        "cat_browser_ratio": [0.5] * rows,
        "cat_ide_ratio": [0.0] * rows,
        "cat_terminal_ratio": [0.0] * rows,
        "cat_comms_ratio": [0.0] * rows,
        "cat_office_ratio": [0.0] * rows,
        "cat_media_ratio": [0.0] * rows,
        "cat_gaming_ratio": [0.0] * rows,
        "cat_remoteaccess_ratio": [0.0] * rows,
        "cat_filemanager_ratio": [0.0] * rows,
        "cat_system_ratio": [0.0] * rows,
        "cat_other_ratio": [0.0] * rows,
        "locked_ratio": [0.0] * rows,
        "active_work_ratio": [0.6] * rows,
        "idle_bucket_mean_sec": [20.0] * rows,
        "idle_ge_300_ratio": [0.0] * rows,
        "has_idle_data": [1] * rows,
    }
    p = tmp_path / filename
    pd.DataFrame(data).to_csv(p, index=False)
    return str(p)


def test_load_participant_csv_adds_participant_id(tmp_path):
    path = _make_csv(tmp_path, "features_all_W60_S30_20260519_210039.csv")
    df = load_participant_csv(path, participant_id="007")
    assert (df["participant_id"] == "007").all()

def test_load_participant_csv_adds_window_profile(tmp_path):
    path = _make_csv(tmp_path, "features_all_W120_S60_20260519_210039.csv")
    df = load_participant_csv(path, participant_id="001")
    assert (df["window_profile"] == "W120_S60").all()

def test_load_participant_csv_renames_window_start_ts(tmp_path):
    path = _make_csv(tmp_path, "features_all_W60_S30_20260519_210039.csv")
    df = load_participant_csv(path, participant_id="001")
    assert "window_start_ts" in df.columns

def test_load_participant_csv_row_count(tmp_path):
    path = _make_csv(tmp_path, "features_all_W60_S30_20260519_210039.csv", rows=5)
    df = load_participant_csv(path, participant_id="001")
    assert len(df) == 5


# ── load_all_participants ─────────────────────────────────────────────────────

def test_load_all_participants_picks_latest_csv(tmp_path):
    """When two CSVs exist for the same profile, picks the later timestamp."""
    older = _make_csv(tmp_path, "features_all_W60_S30_20260101_000000.csv", rows=1)
    newer = _make_csv(tmp_path, "features_all_W60_S30_20260601_000000.csv", rows=3)
    df = load_all_participants(
        base_dir=str(tmp_path),
        participant_dirs={"099": str(tmp_path)},
        profiles=["W60_S30"],
    )
    assert len(df) == 3  # newer file has 3 rows

def test_load_all_participants_concatenates_multiple_participants(tmp_path):
    p1 = tmp_path / "p1"
    p2 = tmp_path / "p2"
    p1.mkdir(); p2.mkdir()
    _make_csv(p1, "features_all_W60_S30_20260519_210039.csv", rows=2)
    _make_csv(p2, "features_all_W60_S30_20260519_210039.csv", rows=3)
    df = load_all_participants(
        base_dir=str(tmp_path),
        participant_dirs={"001": str(p1), "002": str(p2)},
        profiles=["W60_S30"],
    )
    assert len(df) == 5
    assert set(df["participant_id"].unique()) == {"001", "002"}
```

- [ ] **Step 2: Run tests — verify they fail**

```
pytest tests/test_loader.py -v
```

Expected: `ImportError: No module named 'loader'`

- [ ] **Step 3: Create loader.py**

```python
import re
import glob
import pandas as pd
from pathlib import Path

_PROFILE_RE = re.compile(r"features_all_(W\d+_S\d+)_\d{8}_\d{6}\.csv$")

_RENAME = {"WindowStartTs": "window_start_ts"}

# Participant folders in E:/DataBase (folder number → zero-padded participant_id)
_DEFAULT_BASE = "E:/DataBase"
_DEFAULT_PARTICIPANT_DIRS = {
    f"{n:03d}": str(Path(_DEFAULT_BASE) / str(n))
    for n in range(1, 16)
}
DEFAULT_PROFILES = ["W60_S30", "W120_S60", "W30_S15"]


def _extract_profile(filename: str) -> str:
    m = _PROFILE_RE.search(Path(filename).name)
    if not m:
        raise ValueError(f"Cannot extract profile from filename: {filename}")
    return m.group(1)


def _normalize_columns(df: pd.DataFrame) -> pd.DataFrame:
    return df.rename(columns=_RENAME)


def load_participant_csv(path: str, participant_id: str) -> pd.DataFrame:
    profile = _extract_profile(path)
    df = pd.read_csv(path, low_memory=False)
    df = _normalize_columns(df)
    df["participant_id"] = participant_id
    df["window_profile"] = profile
    return df


def load_all_participants(
    base_dir: str = _DEFAULT_BASE,
    participant_dirs: dict[str, str] | None = None,
    profiles: list[str] = DEFAULT_PROFILES,
) -> pd.DataFrame:
    if participant_dirs is None:
        participant_dirs = _DEFAULT_PARTICIPANT_DIRS

    frames = []
    for pid, folder in participant_dirs.items():
        for profile in profiles:
            pattern = str(Path(folder) / f"features_all_{profile}_*.csv")
            matches = sorted(glob.glob(pattern))  # alphabetical = chronological
            if not matches:
                continue
            latest = matches[-1]
            frames.append(load_participant_csv(latest, pid))

    if not frames:
        return pd.DataFrame()
    return pd.concat(frames, ignore_index=True)
```

- [ ] **Step 4: Run tests — verify all pass**

```
pytest tests/test_loader.py -v
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add markovTest/loader.py markovTest/tests/test_loader.py
git commit -m "feat(markov): add CSV loader with column normalisation and participant tagging"
```

---

## Task 4: Analysis functions

**Files:**
- Create: `analysis.py`
- Create: `tests/test_analysis.py`

- [ ] **Step 1: Write failing tests**

Create `tests/test_analysis.py`:

```python
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
    # participant 001 should not have transitions to C or D
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
    # A→B: 1, B→A: 1, A→C: 1 — all rare (count < 5), rare_pct should be 1.0
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
    df.loc[0:2, "is_abnormal"] = 1  # first 3 rows abnormal
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
    # train: A→B only. val: A→B (seen) + A→C (unseen). Rate = 1/2 = 0.5
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
```

- [ ] **Step 2: Run tests — verify they fail**

```
pytest tests/test_analysis.py -v
```

Expected: `ImportError: No module named 'analysis'`

- [ ] **Step 3: Create analysis.py**

```python
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
    train = normal.iloc[:n_train]
    val = normal.iloc[n_train:]
    return train, val


def unseen_transition_rate(
    train_transitions: pd.DataFrame,
    val_transitions: pd.DataFrame,
    participant_id: str,
) -> float:
    train_t = train_transitions[train_transitions["participant_id"] == participant_id]
    val_t = val_transitions[val_transitions["participant_id"] == participant_id]

    if val_t.empty:
        return 0.0

    train_pairs = set(
        zip(train_t["markov_state_vA"], train_t["next_state"])
    )
    val_rows = list(zip(val_t["markov_state_vA"], val_t["next_state"]))
    total = sum(val_t["transition_count"])
    unseen = sum(
        count
        for (src, dst), count in zip(val_rows, val_t["transition_count"])
        if (src, dst) not in train_pairs
    )
    return unseen / total if total > 0 else 0.0
```

- [ ] **Step 4: Run tests — verify all pass**

```
pytest tests/test_analysis.py -v
```

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add markovTest/analysis.py markovTest/tests/test_analysis.py
git commit -m "feat(markov): add analysis pipeline with distribution, transitions, sparsity, and unseen-rate"
```

---

## Task 5: Report writer

**Files:**
- Create: `report.py`

No unit tests needed — this is pure formatting/IO that is verified by running the full pipeline.

- [ ] **Step 1: Create report.py**

```python
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

    normal_unseen = 0.0
    if len(val) > 1:
        train_t = transition_table(train)
        val_t = transition_table(val)
        normal_unseen = unseen_transition_rate(train_t, val_t, participant_id)

    abnormal_unseen = 0.0
    if len(abnormal) > 1:
        train_t = transition_table(train)
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
```

- [ ] **Step 2: Commit**

```bash
git add markovTest/report.py
git commit -m "feat(markov): add per-participant report writer"
```

---

## Task 6: CLI entrypoint and real-data smoke test

**Files:**
- Create: `run.py`

- [ ] **Step 1: Create run.py**

```python
"""
Usage:
    python run.py                          # W60_S30, all 15 participants
    python run.py --profile W120_S60
    python run.py --profile W30_S15
    python run.py --profile W60_S30 --participants 1 2 3
"""
import argparse
import sys
from pathlib import Path

from loader import load_all_participants, _DEFAULT_PARTICIPANT_DIRS, DEFAULT_PROFILES
from states import assign_markov_state_vA
from report import build_participant_report, print_summary, save_reports_csv


def main():
    parser = argparse.ArgumentParser(description="Markov State Version A — real-data analysis")
    parser.add_argument("--profile", choices=DEFAULT_PROFILES, default="W60_S30")
    parser.add_argument("--participants", nargs="*", type=int, default=None,
                        help="Folder numbers to include (e.g. 1 2 3). Default: all.")
    parser.add_argument("--out", default="markov_report_vA.csv",
                        help="Output CSV path for summary report")
    args = parser.parse_args()

    if args.participants:
        dirs = {f"{n:03d}": str(Path("E:/DataBase") / str(n)) for n in args.participants}
    else:
        dirs = _DEFAULT_PARTICIPANT_DIRS

    print(f"Loading profile={args.profile} for {len(dirs)} participant(s)…")
    df = load_all_participants(participant_dirs=dirs, profiles=[args.profile])

    if df.empty:
        print("ERROR: No data loaded. Check E:/DataBase paths.", file=sys.stderr)
        sys.exit(1)

    print(f"Loaded {len(df):,} rows. Assigning markov_state_vA…")
    df["markov_state_vA"] = df.apply(assign_markov_state_vA, axis=1)

    # is_abnormal: set to 0 since top-level CSVs have no abnormal tagging
    if "is_abnormal" not in df.columns:
        df["is_abnormal"] = 0

    print(f"State counts:\n{df['markov_state_vA'].value_counts().head(20)}\n")

    participant_ids = sorted(df["participant_id"].unique())
    reports = [build_participant_report(df, pid) for pid in participant_ids]

    print("\n=== Per-participant summary ===")
    print_summary(reports)

    out_path = Path(args.out)
    save_reports_csv(reports, str(out_path))


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Run full suite — verify all unit tests still pass**

```
pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 3: Smoke test — single participant, W60_S30**

```
python run.py --profile W60_S30 --participants 1
```

Expected output (values will vary):
```
Loading profile=W60_S30 for 1 participant(s)…
Loaded N rows. Assigning markov_state_vA…
State counts:
...
=== Per-participant summary ===
PID    Windows  States  Transitions  RarePct  NormalUnseen  AbnUnseen
-----------------------------------------------------------------------
001      XXXX      XX          XX    XX.X%         XX.X%      XX.X%
Report saved to markov_report_vA.csv
```

If this errors, check `E:/DataBase/1/` is accessible from your working directory (it's an absolute path so it should always resolve).

- [ ] **Step 4: Full run — all 15 participants, W60_S30**

```
python run.py --profile W60_S30 --out results/report_W60_S30.csv
```

Check results against acceptance criteria:
- Each participant has < ~20 unique states ✓/✗
- `rare_transition_pct` < 30–40% for most participants ✓/✗
- `normal_val_unseen_rate` < 15–20% ✓/✗

- [ ] **Step 5: Run W120_S60 for comparison**

```
python run.py --profile W120_S60 --out results/report_W120_S60.csv
```

- [ ] **Step 6: Commit**

```bash
git add markovTest/run.py markovTest/results/
git commit -m "feat(markov): add CLI entrypoint; run full Version A evaluation on real data"
```

---

## Self-Review

**Spec coverage:**
- ✅ WorkMode: all 13 priorities implemented and tested
- ✅ EngagementMode: all 5 priorities implemented and tested
- ✅ `assign_markov_state_vA` composite function with Locked and NoApp collapsing
- ✅ Expected state set (§5) — output verified by real-data run
- ✅ State distribution per participant (§6A) — `state_distribution()`
- ✅ Transition table (§6B) — `transition_table()`
- ✅ Sparsity check (§6C) — `sparsity_check()`
- ✅ Train/validation chronological split (§6D) — `chronological_split()`
- ✅ Unseen transition rate (§6D) — `unseen_transition_rate()`
- ✅ Report fields from §8 all present in `build_participant_report()`
- ✅ W60_S30 first, W120_S60 second, W30_S15 for comparison (CLI flag)
- ✅ Acceptance condition from §8 (`if it passes…`) documented in Task 6 Step 4

**Placeholder scan:** None found — all code blocks are complete.

**Type consistency:**
- `transition_table()` called with full participant df in `build_participant_report` and then filtered by `participant_id` — consistent with test usage.
- `unseen_transition_rate()` takes pre-filtered transitions per participant — consistent across `analysis.py` and `report.py`.
- `chronological_split()` returns `(train_df, val_df)` — used correctly in `report.py`.
