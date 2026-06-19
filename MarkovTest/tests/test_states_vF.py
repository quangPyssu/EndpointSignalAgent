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
