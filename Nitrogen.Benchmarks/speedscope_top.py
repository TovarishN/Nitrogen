#!/usr/bin/env python3
"""Top frames of a speedscope profile by self and inclusive time.

usage: speedscope_top.py <file.speedscope.json> [count] [filter]
Handles both "sampled" and "evented" profiles (TraceEvent writes evented ones). Percentages are of
the time covered by frames whose name contains <filter> (default: every frame).
"""
import collections
import json
import sys


def is_pseudo(name):
    # TraceEvent adds these as leaves (on macOS/arm64 every CPU sample ends in UNMANAGED_CODE_TIME).
    return name.startswith("UNMANAGED") or name in ("CPU_TIME", "BROKEN")


def aggregate(doc, names):
    self_time = collections.Counter()
    inclusive = collections.Counter()
    total = 0.0
    for profile in doc["profiles"]:
        if profile["type"] == "sampled":
            for stack, weight in zip(profile["samples"], profile["weights"]):
                total += weight
                real = [frame for frame in stack if not is_pseudo(names[frame])]
                if real:
                    self_time[real[-1]] += weight
                for frame in set(stack):
                    inclusive[frame] += weight
            continue
        stack = []
        last = profile["startValue"]
        for event in profile["events"]:
            at = event["at"]
            if stack and at > last:
                span = at - last
                total += span
                real = [frame for frame in stack if not is_pseudo(names[frame])]
                if real:
                    self_time[real[-1]] += span
                for frame in set(stack):
                    inclusive[frame] += span
            last = at
            if event["type"] == "O":
                stack.append(event["frame"])
            elif stack and stack[-1] == event["frame"]:
                stack.pop()
            elif event["frame"] in stack:
                stack.remove(event["frame"])
    return self_time, inclusive, total


def top(table, names, base, count, name_filter):
    rows = [(frame, time) for frame, time in table.most_common() if name_filter in names[frame]][:count]
    for frame, time in rows:
        print(f"{100 * time / base:6.2f}%  {names[frame][:150]}")


def main():
    path = sys.argv[1]
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 25
    name_filter = sys.argv[3] if len(sys.argv) > 3 else ""
    doc = json.load(open(path, encoding="utf-8"))
    names = [frame["name"] for frame in doc["shared"]["frames"]]
    self_time, inclusive, total = aggregate(doc, names)
    # Percentages are of the benchmark method's inclusive time, so harness and idle threads drop out.
    roots = [frame for frame in inclusive if names[frame].endswith("MotionParseProfile.NitrogenTree()")]
    base = max((inclusive[frame] for frame in roots), default=total) or 1.0
    print(f"NitrogenTree inclusive: {100 * base / max(total, 1e-9):.1f}% of profiled time")
    print("== SELF (% of NitrogenTree inclusive) ==")
    top(self_time, names, base, count, name_filter)
    print("== INCLUSIVE (% of NitrogenTree inclusive) ==")
    top(inclusive, names, base, count, name_filter)


if __name__ == "__main__":
    main()
