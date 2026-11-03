from datetime import timedelta
import math


TRANSITIONS = {
    "DETECTED": {"CAPTURED", "FAILED"},
    "CAPTURED": {"INSPECTING", "FAILED"},
    "INSPECTING": {"DECIDED", "MANUAL_REVIEW", "FAILED"},
    "DECIDED": {"QUEUED_FOR_SORT", "MANUAL_REVIEW", "FAILED"},
    "QUEUED_FOR_SORT": {"SORTED", "FAILED"},
    "MANUAL_REVIEW": {"DECIDED", "FAILED"},
    "SORTED": set(),
    "FAILED": set(),
}
TIMESTAMPS = {
    "DETECTED": "detected_at",
    "CAPTURED": "captured_at",
    "INSPECTING": "inspecting_at",
    "DECIDED": "decided_at",
    "QUEUED_FOR_SORT": "queued_at",
    "MANUAL_REVIEW": "manual_review_at",
    "SORTED": "sorted_at",
    "FAILED": "failed_at",
}


def advance(track, stage, now, reason=None):
    if stage not in TRANSITIONS.get(track.stage, set()):
        raise ValueError(f"Invalid product tracking transition {track.stage} -> {stage}.")
    if now < max(
        getattr(track, key) for key in TIMESTAMPS.values() if getattr(track, key) is not None
    ):
        raise ValueError("Tracking time must not move backwards.")
    track.stage = stage
    key = TIMESTAMPS[stage]
    if stage in ["DECIDED", "MANUAL_REVIEW"] or getattr(track, key) is None:
        setattr(track, key, now)
    if stage == "DECIDED":
        track.failure_reason = None
    elif stage in ["FAILED", "MANUAL_REVIEW"]:
        track.failure_reason = reason
    track.updated_at = now


def delay_milliseconds(distance, speed):
    if distance is None or speed is None:
        return None
    if not math.isfinite(distance) or not 0 <= distance <= 20000:
        raise ValueError("Sorter distance is outside 0..20000 mm.")
    if not math.isfinite(speed) or not 0 < speed <= 5000:
        return None
    delay = distance / speed * 1000
    if not math.isfinite(delay):
        raise ValueError("The calculated delay must be finite.")
    return delay


def sort_due_at(now, delay):
    if delay is None:
        return None
    if not math.isfinite(delay) or delay < 0:
        raise ValueError("Delay must be finite and non-negative.")
    return now + timedelta(milliseconds=delay)


def actuation(enabled=False):
    return {
        "hardwareCommandIssued": False,
        "actuation": "not_wired" if enabled else "disabled",
        "message": "Hardware actuation is enabled in configuration, but no actuator transport is implemented. No command was sent."
        if enabled
        else "Hardware actuation is disabled. No servo, motor, fan, or gate command was sent.",
    }
