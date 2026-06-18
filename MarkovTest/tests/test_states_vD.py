import pytest
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from states_vD import assign_presence_mode, assign_display_mode, assign_markov_state_vD


def _row(**kwargs):
    defaults = dict(
        locked_ratio=0.0,
        presence_present_ratio=0.0,
        presence_away_ratio=0.0,
        display_on_ratio=0.90,
        display_off_ratio=0.05,
    )
    defaults.update(kwargs)
    return defaults


# ── assign_presence_mode ──────────────────────────────────────────────────────

def test_presence_locked():
    r = _row(locked_ratio=0.85)
    assert assign_presence_mode(r) == "Locked"

def test_presence_locked_exact_threshold():
    r = _row(locked_ratio=0.80)
    assert assign_presence_mode(r) == "Locked"

def test_presence_locked_below_falls_to_confirmed_present():
    r = _row(locked_ratio=0.79, presence_present_ratio=0.80)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present():
    r = _row(presence_present_ratio=0.80)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present_exact_threshold():
    r = _row(presence_present_ratio=0.70)
    assert assign_presence_mode(r) == "ConfirmedPresent"

def test_presence_confirmed_present_below_ambiguous():
    r = _row(presence_present_ratio=0.69, presence_away_ratio=0.20)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_probably_away():
    r = _row(presence_away_ratio=0.80)
    assert assign_presence_mode(r) == "ProbablyAway"

def test_presence_probably_away_exact_threshold():
    r = _row(presence_away_ratio=0.70)
    assert assign_presence_mode(r) == "ProbablyAway"

def test_presence_probably_away_below_ambiguous():
    r = _row(presence_away_ratio=0.69, presence_present_ratio=0.20)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_ambiguous_both_weak():
    r = _row(presence_present_ratio=0.40, presence_away_ratio=0.40)
    assert assign_presence_mode(r) == "AmbiguousPresence"

def test_presence_unknown_both_zero():
    r = _row(presence_present_ratio=0.0, presence_away_ratio=0.0)
    assert assign_presence_mode(r) == "UnknownPresence"


# ── assign_display_mode ───────────────────────────────────────────────────────

def test_display_always_on():
    r = _row(display_on_ratio=0.90)
    assert assign_display_mode(r) == "DisplayAlwaysOn"

def test_display_always_on_exact_threshold():
    r = _row(display_on_ratio=0.85)
    assert assign_display_mode(r) == "DisplayAlwaysOn"

def test_display_always_on_below_mostly_on():
    r = _row(display_on_ratio=0.84, display_off_ratio=0.10)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on():
    r = _row(display_on_ratio=0.65, display_off_ratio=0.30)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on_exact_threshold():
    r = _row(display_on_ratio=0.50)
    assert assign_display_mode(r) == "DisplayMostlyOn"

def test_display_mostly_on_below_display_off():
    r = _row(display_on_ratio=0.30, display_off_ratio=0.65)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_off():
    r = _row(display_on_ratio=0.10, display_off_ratio=0.80)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_off_exact_threshold():
    r = _row(display_on_ratio=0.30, display_off_ratio=0.60)
    assert assign_display_mode(r) == "DisplayOff"

def test_display_mixed():
    r = _row(display_on_ratio=0.40, display_off_ratio=0.40)
    assert assign_display_mode(r) == "DisplayMixed"


# ── assign_markov_state_vD ────────────────────────────────────────────────────

def test_vD_locked_collapses():
    r = _row(locked_ratio=0.90, display_on_ratio=0.0, display_off_ratio=1.0)
    assert assign_markov_state_vD(r) == "Locked"

def test_vD_confirmed_present_always_on():
    r = _row(presence_present_ratio=0.80, display_on_ratio=0.90)
    assert assign_markov_state_vD(r) == "ConfirmedPresent_DisplayAlwaysOn"

def test_vD_probably_away_display_off():
    r = _row(presence_away_ratio=0.80, display_on_ratio=0.10, display_off_ratio=0.80)
    assert assign_markov_state_vD(r) == "ProbablyAway_DisplayOff"

def test_vD_unknown_presence_mostly_on():
    r = _row(presence_present_ratio=0.0, presence_away_ratio=0.0,
             display_on_ratio=0.65, display_off_ratio=0.20)
    assert assign_markov_state_vD(r) == "UnknownPresence_DisplayMostlyOn"

def test_vD_ambiguous_mixed():
    r = _row(presence_present_ratio=0.40, presence_away_ratio=0.35,
             display_on_ratio=0.40, display_off_ratio=0.40)
    assert assign_markov_state_vD(r) == "AmbiguousPresence_DisplayMixed"
