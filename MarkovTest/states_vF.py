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
