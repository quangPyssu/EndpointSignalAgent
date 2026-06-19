# Markov State Versions vW, vE, vF — Implementation and Evaluation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement and evaluate three new Markov state schemas — vW (WorkMode alone, diagnostic), vE (WorkMode × TimeBucket), and vF (WorkMode × TimeBucket × EngagementMode) — to determine whether adding temporal context (UTC+7 local time) to vA's dimensions improves or maintains the anomaly signal.

**Architecture:** Each version follows the same pipeline as vA–vD: pure-function state assignment in `states_vX.py` → unit tests → registered in `run.py` → run on real data → findings document. `states_vE.py` defines `assign_time_bucket` which is imported by `states_vF.py` (no duplication). `run.py` gains a `--base-dir` flag to support the Mac DataBase path.

**Tech Stack:** Python 3.11+, pandas (for Timestamp UTC conversion), pytest. No new dependencies.

## Global Constraints

- Working directory for all commands: `MarkovTest/` (all relative paths are relative to this folder)
- Real data: `/Users/lap15174/EndpointSignalAgent/DataBase/{N}/participant_*/features.db` — always pass `--base-dir /Users/lap15174/EndpointSignalAgent/DataBase` to `run.py`
- Timestamps in the database are UTC; participants are in UTC+7 — use `pd.Timestamp(ts).tz_convert(timezone(timedelta(hours=7)))` to get local hour
- `window_start_ts` is stored as ISO string e.g. `'2026-04-24T13:46:30.0000000+00:00'` (7 decimal places — use pandas, not `datetime.fromisoformat`)
- All state assignment functions accept a plain `dict` row (or pandas Series from `df.apply`) and use `.get()` / `row["key"]` style access
- `assign_work_mode` and `assign_engagement_mode` live in `states.py` — import them, do not copy
- All tests use a `_row(**kwargs)` helper pattern; `window_start_ts` must be in `_row` defaults for vE/vF
- Run full test suite `pytest tests/ -v` before and after every commit
- Findings documents saved to `MarkovTest/results/`

---

## Evaluation criteria (same as vA–vD)

| Criterion | Target | Why it matters |
|---|---|---|
| C1: States per participant | Reasonable (not degenerate) | Too few = no discriminating power; too many = sparse training |
| C2: RarePct < 60% | Most participants pass | High sparsity predicts poor generalization |
| C3: NormalUnseen < 20% | ALL participants pass | Critical feasibility gate — model must generalize to held-out normal |
| C4: AbnUnseen > NormalUnseen | P001, P005, P006, P011 | The anomaly signal criterion |

---

## Prior results (reference for comparison tables in findings)

| Metric | vA | vB | vC | vD |
|---|---|---|---|---|
| Observed states range | 18–35 | 6–10 | 2–7 | 6–8 |
| RarePct range | 52.9%–69.9% | 45.0%–70.7% | 0.0%–63.6% | 12.5%–52.6% |
| NormalUnseen range | 0.0%–5.6% | 0.0%–5.1% | 0.0%–0.1% | 0.0%–0.4% |
| P005 AbnUnseen | **8.3%** | 4.2% | 0.0% | 0.0% |
| P011 AbnUnseen | **12.5%** | 12.5% | 0.0% | 0.0% |

---

## File structure

```
MarkovTest/
  states_vW.py            — assign_markov_state_vW (thin wrapper over assign_work_mode)
  states_vE.py            — assign_time_bucket, assign_markov_state_vE
  states_vF.py            — assign_markov_state_vF (imports from states.py + states_vE.py)
  run.py                  — (MODIFIED) add --base-dir flag; register vW, vE, vF
  tests/
    test_states_vW.py     — composite tests for vW
    test_states_vE.py     — unit tests for assign_time_bucket + composite vE
    test_states_vF.py     — unit tests for composite vF (sub-functions already covered)
  results/
    report_vW_W60_S30.csv
    report_vE_W60_S30.csv
    report_vE_W120_S60.csv
    report_vF_W60_S30.csv
    report_vF_W120_S60.csv
    markov_vW_findings.md
    markov_vE_findings.md
    markov_vF_findings.md
```

---

## Task 1: Extend run.py — add `--base-dir` and register vW/vE/vF

**Files:**
- Modify: `run.py`

**Interfaces:**
- Produces: `--base-dir` CLI argument passed through to `load_all_participants(base_dir=...)`
- Consumed by: all subsequent run steps

- [ ] **Step 1: Update run.py**

Replace the full contents of `run.py`:

```python
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
```

- [ ] **Step 2: Smoke test — verify vA still works**

```
cd MarkovTest && python run.py --version vA --profile W60_S30 --participants 1 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

Expected: participant 001 summary printed, CSV saved to `results/report_vA_W60_S30.csv`.

- [ ] **Step 3: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all existing tests PASS.

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/run.py
git commit -m "feat(markov): add --base-dir flag to run.py; lazy-register vW/vE/vF"
```

---

## Task 2: Version W — WorkMode alone (diagnostic)

**Files:**
- Create: `states_vW.py`
- Create: `tests/test_states_vW.py`

**Interfaces:**
- Consumes: `assign_work_mode` from `states.py`
- Produces: `assign_markov_state_vW(row: dict) -> str`
- Note: `assign_work_mode` boundary cases are fully covered by `test_states.py`; vW tests only cover the composite wrapper

**State definition:**

```
markov_state_vW = WorkMode   (13 possible states, no second dimension)
```

Same 13 WorkMode states as vA: `NoApp`, `RemoteAccessWork`, `DeveloperWork`, `TerminalWork`, `BrowserWork`, `CommsWork`, `OfficeWork`, `MediaWork`, `GamingWork`, `FileWork`, `SystemWork`, `OtherWork`, `MixedWork`.

**Why evaluate this:** Tests the hypothesis that vA's anomaly signal lives primarily in WorkMode transitions (in which case vW ≈ vA), vs. the EngagementMode dimension being load-bearing (in which case vW << vA).

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vW.py`:

```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vW import assign_markov_state_vW


def _row(**kwargs):
    defaults = dict(
        has_app_data=1,
        cat_browser_ratio=0.0, cat_ide_ratio=0.0, cat_terminal_ratio=0.0,
        cat_comms_ratio=0.0, cat_office_ratio=0.0, cat_media_ratio=0.0,
        cat_gaming_ratio=0.0, cat_remoteaccess_ratio=0.0,
        cat_filemanager_ratio=0.0, cat_system_ratio=0.0, cat_other_ratio=0.0,
        locked_ratio=0.0, active_work_ratio=0.0,
        idle_bucket_mean_sec=0.0, idle_ge_300_ratio=0.0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


def test_vW_no_app():
    assert assign_markov_state_vW(_row(has_app_data=0)) == "NoApp"

def test_vW_browser():
    assert assign_markov_state_vW(_row(cat_browser_ratio=0.65)) == "BrowserWork"

def test_vW_developer():
    assert assign_markov_state_vW(_row(cat_ide_ratio=0.30, cat_terminal_ratio=0.25)) == "DeveloperWork"

def test_vW_mixed():
    assert assign_markov_state_vW(_row(cat_browser_ratio=0.30, cat_comms_ratio=0.30)) == "MixedWork"

def test_vW_other():
    assert assign_markov_state_vW(_row(cat_other_ratio=0.60)) == "OtherWork"

def test_vW_system():
    assert assign_markov_state_vW(_row(cat_system_ratio=0.55)) == "SystemWork"

def test_vW_remote_access_priority_over_developer():
    r = _row(cat_remoteaccess_ratio=0.35, cat_ide_ratio=0.40, cat_terminal_ratio=0.20)
    assert assign_markov_state_vW(r) == "RemoteAccessWork"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vW.py -v
```

Expected: `ImportError: No module named 'states_vW'`

- [ ] **Step 3: Create states_vW.py**

```python
from states import assign_work_mode


def assign_markov_state_vW(row) -> str:
    return assign_work_mode(row)
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vW.py -v
```

Expected: all 7 tests PASS.

- [ ] **Step 5: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vW.py MarkovTest/tests/test_states_vW.py
git commit -m "feat(markov): add Version W state assignment (WorkMode alone, diagnostic)"
```

---

## Task 3: Run Version W and write findings

**Files:**
- Create: `results/report_vW_W60_S30.csv`
- Create: `results/markov_vW_findings.md`

- [ ] **Step 1: Run vW on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vW --profile W60_S30 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

Copy the full console output (total windows, top states table, per-participant summary table). You will need all rows.

- [ ] **Step 2: Create results/markov_vW_findings.md**

```markdown
# Markov State Version W — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** [FILL — from "Loaded N rows"]
**State schema:** WorkMode (no second dimension)

---

## What Version W is

Version W is a diagnostic baseline that maps each window to WorkMode alone — the application-category half of Version A — with no EngagementMode dimension.

```
markov_state_vW = WorkMode
```

Same 13 WorkMode states as vA. Purpose: empirically determine how much of vA's anomaly signal (P005 8.3%, P011 12.5%) survives when the EngagementMode dimension is removed.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| [FILL top 10 from console] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row from console summary] | | | | | | |

**— = no annotation data for this participant**

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant (13 theoretical max)

**Result:** [FILL — range X–Y]

**Verdict:** [PASS/MARGINAL/FAIL — expected to be 8–13, lower than vA's 18–35 since EngagementMode is removed]

### Criterion 2: RarePct < 60%

**Result:** [FILL — range X%–Y%]

**Verdict:** [Expected to improve over vA since fewer states means denser transitions]

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** [FILL]

**Verdict:** [Expected to pass — compact state space trains easily]

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal — the diagnostic)

| PID | NormalUnseen | AbnUnseen | Signal | vs. vA AbnUnseen |
|---|---|---|---|---|
| 001 | [FILL] | [FILL] | | vA: 0.7% |
| 005 | [FILL] | [FILL] | | vA: 8.3% |
| 006 | [FILL] | [FILL] | | vA: 0.0% |
| 011 | [FILL] | [FILL] | | vA: 12.5% |

**Verdict:** [FILL — if AbnUnseen drops significantly vs. vA, EngagementMode is load-bearing for the signal; if it holds, WorkMode alone is sufficient]

---

## Comparison to Version A

| Metric | Version A | Version W |
|---|---|---|
| Theoretical max states | 53 | 13 |
| Observed state range | 18–35 | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] |

---

## Overall verdict

[FILL — does removing EngagementMode degrade the signal? State the implication for vF (the triple product): if vW already captures the signal, the time dimension in vE/vF is additive; if vW loses signal, EngagementMode is mandatory and the triple product in vF is the right extension.]

*Generated by `MarkovTest/run.py` · Version W · Profile W60_S30 · 2026-06-19*
```

- [ ] **Step 3: Commit**

```bash
git add MarkovTest/results/report_vW_W60_S30.csv MarkovTest/results/markov_vW_findings.md
git commit -m "data(markov): run Version W evaluation; add findings document"
```

---

## Task 4: Version E — WorkMode × TimeBucket (UTC+7)

**Files:**
- Create: `states_vE.py`
- Create: `tests/test_states_vE.py`

**Interfaces:**
- Consumes: `assign_work_mode` from `states.py`
- Produces: `assign_time_bucket(row) -> str`, `assign_markov_state_vE(row) -> str`
- `assign_time_bucket` is exported and imported by `states_vF.py`

### State definitions

**WorkMode** — same as vA (reused from `states.py`).

**TimeBucket** — local UTC+7 hour of the window start, binned into 4 named periods:

| Bucket | UTC+7 local hours | UTC hours (approximate) |
|---|---|---|
| `EarlyMorning` | 05:00–08:59 | 22:00–01:59 |
| `CoreHours` | 09:00–17:59 | 02:00–10:59 |
| `Evening` | 18:00–22:59 | 11:00–15:59 |
| `Night` | 23:00–04:59 | 16:00–21:59 |

**Composite state:**
- If `assign_engagement_mode(row) == "Locked"` → state = `"Locked"` (screen is locked — time context during lock is not anomaly-relevant)
- Otherwise → `"{WorkMode}_{TimeBucket}"`

**Theoretical maximum: 52 states** (13 WorkMode × 4 TimeBucket) + 1 Locked = 53. In practice expect 15–28 per participant (most participants don't use all WorkModes at all hours).

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vE.py`:

```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vE import assign_time_bucket, assign_markov_state_vE


def _row(**kwargs):
    defaults = dict(
        has_app_data=1,
        # UTC timestamp; UTC+7 offset = +7 hours
        # Default: UTC 02:00 = UTC+7 09:00 = CoreHours
        window_start_ts="2026-04-24T02:00:00+00:00",
        cat_browser_ratio=0.0, cat_ide_ratio=0.0, cat_terminal_ratio=0.0,
        cat_comms_ratio=0.0, cat_office_ratio=0.0, cat_media_ratio=0.0,
        cat_gaming_ratio=0.0, cat_remoteaccess_ratio=0.0,
        cat_filemanager_ratio=0.0, cat_system_ratio=0.0, cat_other_ratio=0.0,
        locked_ratio=0.0, active_work_ratio=0.0,
        idle_bucket_mean_sec=0.0, idle_ge_300_ratio=0.0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


# ── assign_time_bucket ────────────────────────────────────────────────────────
# UTC timestamp → UTC+7 local hour mapping:
#   UTC 22:00 = UTC+7 05:00 → EarlyMorning boundary
#   UTC 02:00 = UTC+7 09:00 → CoreHours
#   UTC 11:00 = UTC+7 18:00 → Evening boundary
#   UTC 16:00 = UTC+7 23:00 → Night boundary
#   UTC 21:00 = UTC+7 04:00 → still Night (hour 4, before 5)

def test_time_bucket_core_hours():
    r = _row(window_start_ts="2026-04-24T02:00:00+00:00")  # UTC+7 09:00
    assert assign_time_bucket(r) == "CoreHours"

def test_time_bucket_core_hours_end():
    r = _row(window_start_ts="2026-04-24T10:59:00+00:00")  # UTC+7 17:59
    assert assign_time_bucket(r) == "CoreHours"

def test_time_bucket_evening_start():
    r = _row(window_start_ts="2026-04-24T11:00:00+00:00")  # UTC+7 18:00
    assert assign_time_bucket(r) == "Evening"

def test_time_bucket_evening_end():
    r = _row(window_start_ts="2026-04-24T15:59:00+00:00")  # UTC+7 22:59
    assert assign_time_bucket(r) == "Evening"

def test_time_bucket_night_start():
    r = _row(window_start_ts="2026-04-24T16:00:00+00:00")  # UTC+7 23:00
    assert assign_time_bucket(r) == "Night"

def test_time_bucket_night_midnight():
    r = _row(window_start_ts="2026-04-24T17:00:00+00:00")  # UTC+7 00:00
    assert assign_time_bucket(r) == "Night"

def test_time_bucket_night_end():
    r = _row(window_start_ts="2026-04-24T21:59:00+00:00")  # UTC+7 04:59
    assert assign_time_bucket(r) == "Night"

def test_time_bucket_early_morning_start():
    r = _row(window_start_ts="2026-04-24T22:00:00+00:00")  # UTC+7 05:00
    assert assign_time_bucket(r) == "EarlyMorning"

def test_time_bucket_early_morning_end():
    r = _row(window_start_ts="2026-04-25T01:59:00+00:00")  # UTC+7 08:59
    assert assign_time_bucket(r) == "EarlyMorning"

def test_time_bucket_core_hours_start_again():
    r = _row(window_start_ts="2026-04-25T02:00:00+00:00")  # UTC+7 09:00
    assert assign_time_bucket(r) == "CoreHours"

def test_time_bucket_handles_7_decimal_places():
    # real format from features.db
    r = _row(window_start_ts="2026-04-24T02:30:00.0000000+00:00")  # UTC+7 09:30
    assert assign_time_bucket(r) == "CoreHours"


# ── assign_markov_state_vE ────────────────────────────────────────────────────

def test_vE_browser_core_hours():
    r = _row(cat_browser_ratio=0.70, window_start_ts="2026-04-24T02:00:00+00:00")
    assert assign_markov_state_vE(r) == "BrowserWork_CoreHours"

def test_vE_developer_night():
    r = _row(cat_ide_ratio=0.30, cat_terminal_ratio=0.30,
             window_start_ts="2026-04-24T17:00:00+00:00")  # UTC+7 00:00 → Night
    assert assign_markov_state_vE(r) == "DeveloperWork_Night"

def test_vE_other_evening():
    r = _row(cat_other_ratio=0.60, window_start_ts="2026-04-24T11:00:00+00:00")
    assert assign_markov_state_vE(r) == "OtherWork_Evening"

def test_vE_mixed_early_morning():
    r = _row(cat_browser_ratio=0.30, cat_comms_ratio=0.30,
             window_start_ts="2026-04-24T22:00:00+00:00")  # UTC+7 05:00
    assert assign_markov_state_vE(r) == "MixedWork_EarlyMorning"

def test_vE_no_app_core_hours():
    r = _row(has_app_data=0, window_start_ts="2026-04-24T02:00:00+00:00")
    assert assign_markov_state_vE(r) == "NoApp_CoreHours"

def test_vE_locked_collapses_no_time():
    r = _row(locked_ratio=0.90, window_start_ts="2026-04-24T16:00:00+00:00")
    assert assign_markov_state_vE(r) == "Locked"

def test_vE_system_work_night():
    r = _row(cat_system_ratio=0.60, window_start_ts="2026-04-24T18:00:00+00:00")  # UTC+7 01:00
    assert assign_markov_state_vE(r) == "SystemWork_Night"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vE.py -v
```

Expected: `ImportError: No module named 'states_vE'`

- [ ] **Step 3: Create states_vE.py**

```python
from datetime import timezone, timedelta

import pandas as pd

from states import assign_work_mode, assign_engagement_mode

_UTC_PLUS_7 = timezone(timedelta(hours=7))


def assign_time_bucket(row) -> str:
    """Convert window_start_ts (UTC) to UTC+7 local hour and return named time bucket."""
    ts_str = row["window_start_ts"] if not isinstance(row, dict) else row.get("window_start_ts", "")
    hour = pd.Timestamp(ts_str).tz_convert(_UTC_PLUS_7).hour
    if 5 <= hour <= 8:
        return "EarlyMorning"
    if 9 <= hour <= 17:
        return "CoreHours"
    if 18 <= hour <= 22:
        return "Evening"
    return "Night"  # hours 23, 0, 1, 2, 3, 4


def assign_markov_state_vE(row) -> str:
    engagement = assign_engagement_mode(row)
    if engagement == "Locked":
        return "Locked"
    work = assign_work_mode(row)
    bucket = assign_time_bucket(row)
    return f"{work}_{bucket}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vE.py -v
```

Expected: all 18 tests PASS.

- [ ] **Step 5: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vE.py MarkovTest/tests/test_states_vE.py
git commit -m "feat(markov): add Version E state assignment (WorkMode x TimeBucket UTC+7)"
```

---

## Task 5: Run Version E and write findings

**Files:**
- Create: `results/report_vE_W60_S30.csv`
- Create: `results/report_vE_W120_S60.csv`
- Create: `results/markov_vE_findings.md`

- [ ] **Step 1: Run vE on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vE --profile W60_S30 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

Copy full console output.

- [ ] **Step 2: Run vE on W120_S60**

```
cd MarkovTest && python run.py --version vE --profile W120_S60 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

- [ ] **Step 3: Create results/markov_vE_findings.md**

```markdown
# Markov State Version E — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** [FILL]
**State schema:** WorkMode × TimeBucket (UTC+7)

---

## What Version E is

Version E extends Version A's WorkMode with a temporal dimension — the local time of day for participants (UTC+7). The hypothesis is that anomalous events at unusual hours (e.g. DeveloperWork at Night) will produce unseen transitions not present in normal training data.

```
markov_state_vE = WorkMode + TimeBucket(UTC+7)
```

**WorkMode** — same 13 states as Version A.

**TimeBucket** — local UTC+7 hour binned into 4 named periods:

| Bucket | UTC+7 local hours |
|---|---|
| `EarlyMorning` | 05:00–08:59 |
| `CoreHours` | 09:00–17:59 |
| `Evening` | 18:00–22:59 |
| `Night` | 23:00–04:59 |

**Collapsing:** `Locked` short-circuits to `"Locked"` (no time context needed for lock windows).

**Theoretical maximum: 53 states** (13 WorkMode × 4 TimeBucket + 1 Locked). In practice expect 15–28 per participant.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| [FILL top 15 from console] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row] | | | | | | |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant

**Result:** [FILL — range X–Y]

**Verdict:** [PASS/MARGINAL/FAIL. Expected 15–28; if higher, participants use all WorkModes across all time slots (overnight study data would cause this).]

### Criterion 2: RarePct < 60%

**Result:** [FILL]

**Verdict:** [Expected to be similar to vA or slightly higher since state space is same size but split by time.]

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** [FILL]

**Verdict:** [PASS/MARGINAL/FAIL. If NormalUnseen rises significantly vs. vA, the time dimension created training gaps — night/early morning work patterns may be undersampled in the training split.]

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal | vs. vA AbnUnseen |
|---|---|---|---|---|
| 001 | [FILL] | [FILL] | | vA: 0.7% |
| 005 | [FILL] | [FILL] | | vA: 8.3% |
| 006 | [FILL] | [FILL] | | vA: 0.0% |
| 011 | [FILL] | [FILL] | | vA: 12.5% |

**Verdict:** [FILL — does the temporal dimension add signal over vA? If P001 or P006 now show AbnUnseen > 0 (they showed 0 in vA), the anomalies in those participants occurred at unusual hours.]

---

## Comparison to Version A

| Metric | Version A | Version E |
|---|---|---|
| Theoretical max states | 53 | 53 |
| Observed state range | 18–35 | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] |

---

## Overall verdict

[FILL — does replacing EngagementMode with TimeBucket improve, match, or degrade the anomaly signal vs. vA? What does the result imply about whether vF (the full triple product) is worth the added complexity?]

*Generated by `MarkovTest/run.py` · Version E · Profile W60_S30 · 2026-06-19*
```

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/results/report_vE_W60_S30.csv MarkovTest/results/report_vE_W120_S60.csv \
    MarkovTest/results/markov_vE_findings.md
git commit -m "data(markov): run Version E evaluation; add findings document"
```

---

## Task 6: Version F — WorkMode × TimeBucket × EngagementMode (triple product)

**Files:**
- Create: `states_vF.py`
- Create: `tests/test_states_vF.py`

**Interfaces:**
- Consumes: `assign_work_mode`, `assign_engagement_mode` from `states.py`; `assign_time_bucket` from `states_vE.py`
- Produces: `assign_markov_state_vF(row) -> str`

### State definition

```
markov_state_vF = WorkMode + TimeBucket(UTC+7) + EngagementMode
```

All three dimensions from the prior versions — the user's stated target combination.

**Collapsing rules (same priority as vA):**
1. If `EngagementMode == Locked` → state = `"Locked"` (no app or time context during lock)
2. If `WorkMode == NoApp` → state = `"NoApp_{TimeBucket}_{EngagementMode}"` (app category meaningless but time/engagement still informative)
3. Otherwise → `"{WorkMode}_{TimeBucket}_{EngagementMode}"`

**Theoretical maximum:**
- 1 (Locked)
- 4 × 4 = 16 (NoApp × TimeBucket × 4 non-Locked EngagementModes)
- 12 × 4 × 4 = 192 (non-NoApp WorkModes × TimeBucket × non-Locked EngagementModes)
- Total: **209 states**

In practice per participant: **25–60 states** (most WorkMode × TimeBucket × EngagementMode combinations never occur; a browser-only participant who only works CoreHours produces far fewer). NormalUnseen may be elevated for smaller participants — the findings will quantify this.

- [ ] **Step 1: Write failing tests**

Create `tests/test_states_vF.py`:

```python
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vF import assign_markov_state_vF


def _row(**kwargs):
    defaults = dict(
        has_app_data=1,
        # UTC 02:00 = UTC+7 09:00 = CoreHours
        window_start_ts="2026-04-24T02:00:00+00:00",
        cat_browser_ratio=0.0, cat_ide_ratio=0.0, cat_terminal_ratio=0.0,
        cat_comms_ratio=0.0, cat_office_ratio=0.0, cat_media_ratio=0.0,
        cat_gaming_ratio=0.0, cat_remoteaccess_ratio=0.0,
        cat_filemanager_ratio=0.0, cat_system_ratio=0.0, cat_other_ratio=0.0,
        locked_ratio=0.0, active_work_ratio=0.60,
        idle_bucket_mean_sec=0.0, idle_ge_300_ratio=0.0, has_idle_data=1,
    )
    defaults.update(kwargs)
    return defaults


def test_vF_locked_collapses():
    r = _row(locked_ratio=0.90, window_start_ts="2026-04-24T17:00:00+00:00")
    assert assign_markov_state_vF(r) == "Locked"

def test_vF_no_app_core_hours_active():
    r = _row(has_app_data=0, window_start_ts="2026-04-24T02:00:00+00:00",
             active_work_ratio=0.60)
    assert assign_markov_state_vF(r) == "NoApp_CoreHours_Active"

def test_vF_no_app_night_long_idle():
    r = _row(has_app_data=0, window_start_ts="2026-04-24T17:00:00+00:00",
             idle_ge_300_ratio=0.60, active_work_ratio=0.10)
    assert assign_markov_state_vF(r) == "NoApp_Night_LongIdle"

def test_vF_browser_core_hours_active():
    r = _row(cat_browser_ratio=0.70, window_start_ts="2026-04-24T02:00:00+00:00",
             active_work_ratio=0.60)
    assert assign_markov_state_vF(r) == "BrowserWork_CoreHours_Active"

def test_vF_developer_night_light_idle():
    r = _row(cat_ide_ratio=0.30, cat_terminal_ratio=0.30,
             window_start_ts="2026-04-24T17:00:00+00:00",  # UTC+7 00:00 → Night
             idle_bucket_mean_sec=90.0, active_work_ratio=0.30)
    assert assign_markov_state_vF(r) == "DeveloperWork_Night_LightIdle"

def test_vF_browser_evening_long_idle():
    r = _row(cat_browser_ratio=0.70,
             window_start_ts="2026-04-24T11:00:00+00:00",  # UTC+7 18:00 → Evening
             idle_ge_300_ratio=0.60, active_work_ratio=0.10)
    assert assign_markov_state_vF(r) == "BrowserWork_Evening_LongIdle"

def test_vF_other_early_morning_unknown_engagement():
    r = _row(cat_other_ratio=0.60,
             window_start_ts="2026-04-24T22:00:00+00:00",  # UTC+7 05:00 → EarlyMorning
             has_idle_data=0, active_work_ratio=0.30)
    assert assign_markov_state_vF(r) == "OtherWork_EarlyMorning_UnknownEngagement"

def test_vF_system_night_active():
    r = _row(cat_system_ratio=0.60,
             window_start_ts="2026-04-24T18:00:00+00:00",  # UTC+7 01:00 → Night
             active_work_ratio=0.60)
    assert assign_markov_state_vF(r) == "SystemWork_Night_Active"

def test_vF_mixed_core_hours_light_idle():
    r = _row(cat_browser_ratio=0.30, cat_comms_ratio=0.30,
             window_start_ts="2026-04-24T05:00:00+00:00",  # UTC+7 12:00 → CoreHours
             idle_bucket_mean_sec=90.0, active_work_ratio=0.30)
    assert assign_markov_state_vF(r) == "MixedWork_CoreHours_LightIdle"

def test_vF_remote_access_core_hours_active():
    r = _row(cat_remoteaccess_ratio=0.40,
             window_start_ts="2026-04-24T04:00:00+00:00",  # UTC+7 11:00 → CoreHours
             active_work_ratio=0.60)
    assert assign_markov_state_vF(r) == "RemoteAccessWork_CoreHours_Active"
```

- [ ] **Step 2: Run tests — verify they fail**

```
cd MarkovTest && pytest tests/test_states_vF.py -v
```

Expected: `ImportError: No module named 'states_vF'`

- [ ] **Step 3: Create states_vF.py**

```python
from states import assign_work_mode, assign_engagement_mode
from states_vE import assign_time_bucket


def assign_markov_state_vF(row) -> str:
    engagement = assign_engagement_mode(row)
    if engagement == "Locked":
        return "Locked"
    work = assign_work_mode(row)
    bucket = assign_time_bucket(row)
    if work == "NoApp":
        return f"NoApp_{bucket}_{engagement}"
    return f"{work}_{bucket}_{engagement}"
```

- [ ] **Step 4: Run tests — verify all pass**

```
cd MarkovTest && pytest tests/test_states_vF.py -v
```

Expected: all 10 tests PASS.

- [ ] **Step 5: Run full test suite**

```
cd MarkovTest && pytest tests/ -v
```

Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add MarkovTest/states_vF.py MarkovTest/tests/test_states_vF.py
git commit -m "feat(markov): add Version F state assignment (WorkMode x TimeBucket x EngagementMode)"
```

---

## Task 7: Run Version F and write findings

**Files:**
- Create: `results/report_vF_W60_S30.csv`
- Create: `results/report_vF_W120_S60.csv`
- Create: `results/markov_vF_findings.md`

- [ ] **Step 1: Run vF on all participants, W60_S30**

```
cd MarkovTest && python run.py --version vF --profile W60_S30 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

Copy full console output.

- [ ] **Step 2: Run vF on W120_S60**

```
cd MarkovTest && python run.py --version vF --profile W120_S60 \
    --base-dir /Users/lap15174/EndpointSignalAgent/DataBase
```

- [ ] **Step 3: Create results/markov_vF_findings.md**

```markdown
# Markov State Version F — Evaluation Findings

**Profile evaluated:** W60_S30 (60-second windows, 30-second slide)
**Participants:** 15
**Total windows:** [FILL]
**State schema:** WorkMode × TimeBucket (UTC+7) × EngagementMode

---

## What Version F is

Version F is the full triple-product schema combining all three behavioral dimensions: what the user is doing (WorkMode), when they are doing it (TimeBucket, UTC+7), and how actively they are engaged (EngagementMode). This is the maximum behavioral context version.

```
markov_state_vF = WorkMode + TimeBucket(UTC+7) + EngagementMode
```

**WorkMode** — same 13 states as Version A.

**TimeBucket** — same 4 UTC+7 time buckets as Version E (EarlyMorning / CoreHours / Evening / Night).

**EngagementMode** — same 5 states as Version A (Locked / LongIdle / LightIdle / Active / UnknownEngagement).

**Collapsing:** Locked → `"Locked"`; NoApp → `"NoApp_{TimeBucket}_{EngagementMode}"`; otherwise `"{WorkMode}_{TimeBucket}_{EngagementMode}"`.

**Theoretical maximum: 209 states.** In practice per participant: 25–60 observed states (most 3-way combinations don't occur). NormalUnseen may be elevated for small participants — this is the key risk being evaluated.

---

## Observed state space

Top states by frequency across all participants (W60_S30):

| State | Count |
|---|---|
| [FILL top 15 from console] | |

---

## Per-participant results

| PID | Windows | States | Transitions | RarePct | NormalUnseen | AbnUnseen |
|---|---|---|---|---|---|---|
| [FILL each row] | | | | | | |

---

## Acceptance criteria assessment

### Criterion 1: Unique states per participant

**Result:** [FILL — range X–Y]

**Verdict:** [PASS/MARGINAL/FAIL. Expected 25–60. If > 60 on average, the triple product has created a state explosion that will cause high NormalUnseen.]

### Criterion 2: RarePct < 60%

**Result:** [FILL]

**Verdict:** [Expected to be higher than vA due to the larger state space. Key question is whether NormalUnseen compensates.]

### Criterion 3: NormalUnseen < 20% (critical)

**Result:** [FILL]

**Verdict:** [PASS/MARGINAL/FAIL. This is the gate. If NormalUnseen rises above 20% for any participant, the triple product is too sparse for that participant's data — the time-of-day dimension created patterns the training split never saw.]

### Criterion 4: AbnUnseen > NormalUnseen (anomaly signal)

| PID | NormalUnseen | AbnUnseen | Signal | vs. vA | vs. vE |
|---|---|---|---|---|---|
| 001 | [FILL] | [FILL] | | vA: 0.7% | vE: [FILL] |
| 005 | [FILL] | [FILL] | | vA: 8.3% | vE: [FILL] |
| 006 | [FILL] | [FILL] | | vA: 0.0% | vE: [FILL] |
| 011 | [FILL] | [FILL] | | vA: 12.5% | vE: [FILL] |

**Verdict:** [FILL — does the triple product add signal over vA and vE, or does the increased sparsity raise NormalUnseen to the point where AbnUnseen is no longer a reliable anomaly indicator?]

---

## Comparison to all versions

| Metric | vA | vE | vF |
|---|---|---|---|
| Theoretical max states | 53 | 53 | 209 |
| Observed state range | 18–35 | [FILL] | [FILL] |
| RarePct range | 52.9%–69.9% | [FILL] | [FILL] |
| NormalUnseen range | 0.0%–5.6% | [FILL] | [FILL] |
| P005 AbnUnseen | 8.3% | [FILL] | [FILL] |
| P011 AbnUnseen | 12.5% | [FILL] | [FILL] |

---

## Overall verdict

[FILL — is the triple product feasible? Does it beat vA and vE, or does the state explosion hurt more than the added context helps? Recommend whether vF should be used as a replacement for vA, a supplement, or discarded. If NormalUnseen fails, recommend a fallback: either vE (2-way with time) or vG (coarsened WorkMode × EngagementMode without time).]

*Generated by `MarkovTest/run.py` · Version F · Profile W60_S30 · 2026-06-19*
```

- [ ] **Step 4: Commit**

```bash
git add MarkovTest/results/report_vF_W60_S30.csv MarkovTest/results/report_vF_W120_S60.csv \
    MarkovTest/results/markov_vF_findings.md
git commit -m "data(markov): run Version F evaluation; add findings document"
```

---

## Self-Review

### Spec coverage

- ✅ vW: WorkMode alone — answers "what about just WorkMode?" empirically
- ✅ vE: WorkMode × TimeBucket — suggested schema from conversation analysis
- ✅ vF: WorkMode × TimeBucket × EngagementMode — explicitly requested by user as "workmode-time-engagement"
- ✅ UTC+7 timezone handling — `assign_time_bucket` uses `pd.Timestamp.tz_convert` with `timezone(timedelta(hours=7))`
- ✅ `window_start_ts` format with 7 decimal places — handled by pandas (tested explicitly in `test_vE_handles_7_decimal_places`)
- ✅ `--base-dir` flag added to `run.py` — Mac DataBase path `/Users/lap15174/EndpointSignalAgent/DataBase` supported
- ✅ vF imports `assign_time_bucket` from `states_vE.py` — no duplication
- ✅ All new state files follow the same `assign_fn(row: dict) -> str` interface as vA–vD
- ✅ Unit tests cover all boundary values for time buckets (all 4 bucket boundaries, 7-decimal-place format)
- ✅ `run.py` uses lazy import for vW/vE/vF so prior versions still work before new files exist
- ✅ Both W60_S30 and W120_S60 profiles run for vE and vF
- ✅ Findings templates include comparison columns vs. vA and vs. prior new version

### Placeholder scan

Findings templates contain `[FILL]` markers — all are labeled with the console output location that supplies the value. No `[FILL]` markers appear in code blocks.

### Type consistency

- `assign_markov_state_vW/vE/vF(row) -> str` — same signature as vA–vD (no type annotation on `row` since it accepts both `dict` and pandas Series)
- `assign_time_bucket(row) -> str` — same row-dict interface; exported from `states_vE`, imported by `states_vF`
- `run.py` calls `df.apply(assign_fn, axis=1)` — unchanged; `window_start_ts` is a column in the dataframe (loaded by `load_participant_db` via `SELECT window_start_ts, ...`)
- `_row()` defaults in all test files include `window_start_ts` for vE/vF tests
- Time bucket boundary math verified: UTC 02:00 + 7h = UTC+7 09:00 (CoreHours ✓), UTC 11:00 + 7h = UTC+7 18:00 (Evening ✓), UTC 16:00 + 7h = UTC+7 23:00 (Night ✓), UTC 22:00 + 7h = UTC+7 05:00 (EarlyMorning ✓)
