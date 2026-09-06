#!/usr/bin/env python3
"""Compute capacity from explicit, timezone-aware intervals; never change a calendar."""

import argparse
import json
import math
import sys
from datetime import datetime, timezone
from pathlib import Path


def instant(value):
    if not isinstance(value, str):
        raise ValueError("interval endpoints must be ISO-8601 strings")
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None or result.utcoffset() is None:
        raise ValueError("interval endpoints require an explicit UTC offset")
    return result.astimezone(timezone.utc)


def intervals(values, label):
    if not isinstance(values, list):
        raise ValueError(label + " must be an array of start/end objects")
    result = []
    for value in values:
        if not isinstance(value, dict) or "start" not in value or "end" not in value:
            raise ValueError(label + " requires start and end on every interval")
        start, end = instant(value["start"]), instant(value["end"])
        if end <= start:
            raise ValueError(label + " interval end must be after start")
        result.append((start, end))
    return merge(result)


def merge(values):
    result = []
    for start, end in sorted(values):
        if result and start <= result[-1][1]:
            result[-1] = (result[-1][0], max(end, result[-1][1]))
        else:
            result.append((start, end))
    return result


def intersection(left, right):
    result = []
    i = j = 0
    while i < len(left) and j < len(right):
        start, end = max(left[i][0], right[j][0]), min(left[i][1], right[j][1])
        if start < end:
            result.append((start, end))
        if left[i][1] <= right[j][1]:
            i += 1
        else:
            j += 1
    return merge(result)


def subtract(available, unavailable):
    result = []
    for start, end in available:
        cursor = start
        for busy_start, busy_end in unavailable:
            if busy_end <= cursor:
                continue
            if busy_start >= end:
                break
            if busy_start > cursor:
                result.append((cursor, busy_start))
            cursor = max(cursor, busy_end)
            if cursor >= end:
                break
        if cursor < end:
            result.append((cursor, end))
    return result


def minutes(values):
    return sum((end - start).total_seconds() for start, end in values) / 60


def number(value, label, positive=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(label + " must be a finite number")
    if value < 0 or (positive and value == 0):
        raise ValueError(label + " must be " + ("positive" if positive else "nonnegative"))
    return value


def serialise(values):
    return [
        {"start": start.isoformat(), "end": end.isoformat(),
         "minutes": (end - start).total_seconds() / 60}
        for start, end in values
    ]


def calculate(data):
    if not isinstance(data, dict):
        raise ValueError("input must be an object")
    coverage = data.get("coverage")
    if coverage not in ("complete", "partial", "unknown"):
        raise ValueError("coverage must explicitly be complete, partial, or unknown")
    if "working" not in data or "busy" not in data:
        raise ValueError("working and busy intervals are required, even when empty")
    working = intervals(data["working"], "working")
    busy = intersection(working, intervals(data["busy"], "busy"))
    free = subtract(working, busy)
    protected = intersection(working, intervals(data.get("protected", []), "protected"))
    protected_free = intersection(free, protected)
    focus_min = number(data.get("minimum_focus_minutes", 90), "minimum_focus_minutes", True)
    focus = [value for value in free if minutes([value]) >= focus_min]
    estimates = data.get("estimates", [])
    if not isinstance(estimates, list):
        raise ValueError("estimates must be an array")
    known = 0
    unknown = []
    seen = set()
    for estimate in estimates:
        if not isinstance(estimate, dict) or not isinstance(estimate.get("id"), str) or not estimate["id"]:
            raise ValueError("each estimate requires a nonempty id")
        if estimate["id"] in seen:
            raise ValueError("duplicate effort estimate: " + estimate["id"])
        seen.add(estimate["id"])
        if estimate.get("minutes") is None:
            unknown.append(estimate["id"])
        else:
            known += number(estimate["minutes"], "effort minutes")
    free_minutes = minutes(free)
    warnings = []
    if coverage != "complete":
        warnings.append("Calendar coverage is incomplete; free time is not established availability.")
    if unknown:
        warnings.append("Some work has no confirmed effort estimate; feasibility is unknown.")
    feasible = None if coverage != "complete" or unknown else known <= free_minutes
    return {
        "coverage": coverage,
        "working_minutes": minutes(working),
        "busy_minutes": minutes(busy),
        "available_minutes": free_minutes,
        "protected_available_minutes": minutes(protected_free),
        "protected_conflict_minutes": minutes(protected) - minutes(protected_free),
        "focus_blocks": serialise(focus),
        "available_blocks": serialise(free),
        "known_effort_minutes": known,
        "unestimated_items": unknown,
        "minimum_overflow_minutes": max(0, known - free_minutes),
        "fits_total_capacity": feasible,
        "allocation_status": "not_allocated",
        "warnings": warnings,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="JSON file, or - for stdin")
    args = parser.parse_args()
    try:
        text = sys.stdin.read() if args.input == "-" else Path(args.input).read_text(encoding="utf-8")
        print(json.dumps(calculate(json.loads(text)), indent=2, allow_nan=False))
    except (ValueError, OSError) as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
