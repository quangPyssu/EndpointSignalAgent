import glob
import json
import sqlite3
from datetime import timezone
from pathlib import Path
from typing import Optional

import pandas as pd

DEFAULT_BASE = "E:/DataBase"
DEFAULT_PROFILES = ["W60_S30", "W120_S60", "W30_S15"]
_ALL_PARTICIPANT_NUMBERS = list(range(1, 16))


def _find_participant_subfolder(folder: str) -> Optional[str]:
    # Windows layout: DataBase/N/participant_*/
    matches = glob.glob(str(Path(folder) / "participant_*" / ""))
    if matches:
        return matches[0].rstrip("/\\")
    # Mac flat layout: DataBase/N/ (annotations live directly here)
    ann = glob.glob(str(Path(folder) / "session_*.annotations.json"))
    return folder if ann else None


def _load_abnormal_intervals(participant_subfolder: str) -> list[tuple]:
    """Return list of (start, end) UTC datetimes for complete abnormal segments."""
    intervals = []
    for af in glob.glob(str(Path(participant_subfolder) / "session_*.annotations.json")):
        with open(af) as f:
            segments = json.load(f)
        for seg in segments:
            if seg.get("segmentType") == "abnormal" and seg.get("isComplete"):
                start = pd.Timestamp(seg["startedAtUtc"]).tz_convert(timezone.utc)
                end = pd.Timestamp(seg["endedAtUtc"]).tz_convert(timezone.utc)
                intervals.append((start, end))
    return intervals


def _tag_abnormal(df: pd.DataFrame, intervals: list[tuple]) -> pd.DataFrame:
    """Add is_abnormal=1 for rows whose window_start_ts falls within any abnormal interval."""
    df = df.copy()
    if not intervals:
        df["is_abnormal"] = 0
        return df
    ts = pd.to_datetime(df["window_start_ts"], utc=True)
    mask = pd.Series(False, index=df.index)
    for start, end in intervals:
        mask |= (ts >= start) & (ts <= end)
    df["is_abnormal"] = mask.astype(int)
    return df


def _find_db(folder: str) -> Optional[str]:
    # Windows layout: DataBase/N/participant_*/features.db
    matches = glob.glob(str(Path(folder) / "participant_*" / "features.db"))
    if matches:
        return matches[0]
    # Mac flat layout: DataBase/N/features.db
    flat = Path(folder) / "features.db"
    return str(flat) if flat.exists() else None



def load_participant_db(
    db_path: str,
    participant_id: str,
    profiles: Optional[list] = None,
) -> pd.DataFrame:
    if profiles is None:
        profiles = DEFAULT_PROFILES

    placeholders = ",".join("?" * len(profiles))
    query = f"""
        SELECT window_start_ts, window_profile_id, features_json
        FROM feature_rows
        WHERE window_profile_id IN ({placeholders})
    """
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    rows = pd.read_sql_query(query, con, params=profiles)
    con.close()

    if rows.empty:
        return pd.DataFrame()

    features = rows["features_json"].apply(json.loads).apply(pd.Series)
    df = pd.concat([rows.drop(columns=["features_json"]), features], axis=1)
    df = df.rename(columns={"window_profile_id": "window_profile"})
    df["participant_id"] = participant_id
    return df


def load_all_participants(
    base_dir: str = DEFAULT_BASE,
    participant_folder_numbers: Optional[list] = None,
    profiles: list = DEFAULT_PROFILES,
) -> pd.DataFrame:
    if participant_folder_numbers is None:
        participant_folder_numbers = _ALL_PARTICIPANT_NUMBERS

    frames = []
    for n in participant_folder_numbers:
        folder = str(Path(base_dir) / str(n))
        db_path = _find_db(folder)
        if db_path is None:
            continue
        pid = f"{n:03d}"
        df = load_participant_db(db_path, pid, profiles)
        if df.empty:
            continue
        subfolder = _find_participant_subfolder(folder)
        intervals = _load_abnormal_intervals(subfolder) if subfolder else []
        df = _tag_abnormal(df, intervals)
        frames.append(df)

    return pd.concat(frames, ignore_index=True) if frames else pd.DataFrame()
