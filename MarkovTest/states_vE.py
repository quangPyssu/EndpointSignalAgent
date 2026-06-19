from datetime import timezone, timedelta

import pandas as pd

from states import assign_work_mode, assign_engagement_mode

_UTC_PLUS_7 = timezone(timedelta(hours=7))


def assign_time_bucket(row) -> str:
    """Convert window_start_ts (UTC) to UTC+7 local hour and return named time bucket."""
    ts_str = row["window_start_ts"]
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
