#!/usr/bin/env python3
"""Dataset validation for the One Timeline universe packs.

REGRESSION GUARD (added 2026-11-04, after the CSI crossover
audit): no shipped dataset may contain an eventGroup with
eventTotal 1. A one-episode "event" renders in the app as
"Crossover · Part 1 of 1", which is nonsense — an event is one
story told across two or more episodes; a single episode that
features another show's characters is a CAMEO, never an event
(see csi-crossovers-research.md and docs/CREATING-A-UNIVERSE.md).

Also checked, for every eventGroup in every dataset:
  - eventKind/eventIndex/eventTotal are set exactly when
    eventGroup is set (no half-flagged episodes) — hard fail
  - group coherence (member count vs eventTotal, index sets) —
    reported as WARNINGS, not failures: the shipped Chicago
    dataset carries one known pre-existing quirk of this kind
    (group 'note-788', a lone episode marked part 3 of 3, from
    the community sheet's note derivation on unaired episodes).
    It is not the 'Part 1 of 1' defect and Chicago data is not
    this guard's to change.

Usage: validate_datasets.py [dataset.json ...]
With no arguments it validates the two SHIPPED datasets (the
copies in the production project's Resources/Raw). Exit code is
nonzero if any check fails.
"""
import json
import sys
from collections import defaultdict

DEFAULTS = [
    "/home/hatch/workspace/projects/one-chicago-production/Resources/Raw/episodes.json",
    "/home/hatch/workspace/projects/one-chicago-production/Resources/Raw/episodes-csi.json",
]


def validate(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    episodes = data["episodes"]
    problems = []
    warnings = []

    groups = defaultdict(list)
    for ep in episodes:
        flagged = [ep.get(f) is not None
                   for f in ("eventGroup", "eventKind", "eventIndex", "eventTotal")]
        if any(flagged) and not all(flagged):
            problems.append(f"{ep['key']}: partially set event fields")
        if ep.get("eventTotal") == 1:
            problems.append(
                f"REGRESSION: {ep['key']} has eventTotal 1 (group "
                f"{ep.get('eventGroup')!r}) — a one-episode event renders "
                f"as 'Part 1 of 1'; it should be a cameo")
        if ep.get("eventGroup") is not None:
            groups[ep["eventGroup"]].append(ep)

    for group, members in groups.items():
        totals = {m["eventTotal"] for m in members}
        if totals != {len(members)}:
            warnings.append(
                f"event {group!r}: eventTotal values {sorted(totals)} "
                f"!= member count {len(members)}")
        indices = sorted(m["eventIndex"] for m in members)
        if indices != list(range(1, len(members) + 1)):
            warnings.append(
                f"event {group!r}: eventIndex values {indices} "
                f"are not 1..{len(members)}")

    n_flagged = sum(len(v) for v in groups.values())
    n_cameos = sum(1 for ep in episodes if ep.get("cameos"))
    status = "PASS" if not problems else "FAIL"
    print(f"{status}  {path}: {len(episodes)} episodes, "
          f"{len(groups)} event groups / {n_flagged} flagged, "
          f"{n_cameos} cameo episodes")
    for p in problems:
        print(f"  !! {p}")
    for w in warnings:
        print(f"  ~~ warning: {w}")
    return not problems


def main():
    paths = sys.argv[1:] or DEFAULTS
    ok = all([validate(p) for p in paths])
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
