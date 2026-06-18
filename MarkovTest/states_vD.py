def assign_presence_mode(row: dict) -> str:
    if row.get("locked_ratio", 0.0) >= 0.80:
        return "Locked"
    present = row.get("presence_present_ratio", 0.0)
    away = row.get("presence_away_ratio", 0.0)
    if present >= 0.70:
        return "ConfirmedPresent"
    if away >= 0.70:
        return "ProbablyAway"
    if present > 0 or away > 0:
        return "AmbiguousPresence"
    return "UnknownPresence"


def assign_display_mode(row: dict) -> str:
    on = row.get("display_on_ratio", 0.0)
    off = row.get("display_off_ratio", 0.0)
    if on >= 0.85:
        return "DisplayAlwaysOn"
    if on >= 0.50:
        return "DisplayMostlyOn"
    if off >= 0.60:
        return "DisplayOff"
    return "DisplayMixed"


def assign_markov_state_vD(row: dict) -> str:
    presence = assign_presence_mode(row)
    display = assign_display_mode(row)
    if presence == "Locked":
        return "Locked"
    return f"{presence}_{display}"
