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
