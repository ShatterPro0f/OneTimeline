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

GENERAL GUARDS (added 2026-11-04 with the MCU pack, Decision 1G):
for every dataset, hard fail on
  - order values not forming exactly 1..N (gapless, unique)
  - duplicate episode keys, or a key that does not equal
    "<show>|<season>|<episodeNumber>"
  - a links[] entry whose platform is not prime/netflix/disney,
    whose ID does not match the platform's form (Prime GTI,
    Netflix bare numeric ID, Disney+ bare UUID), or more than
    one link for the same platform on one entry — the legacy
    primeContentId field counts as a prime link here
  - the same (platform, ID) pair appearing on two entries
  - a category value outside film/episode/special/one-shot/web
Entries with no runtimeMinutes are reported as a warning count
(web extras and unreleased episodes legitimately lack one).

Usage: validate_datasets.py [dataset.json ...]
With no arguments it validates the three SHIPPED datasets (the
masters in ~/workspace/one-chicago/ — since v2.1.0 every
universe ships as a downloadable pack, never embedded). Exit code is
nonzero if any check fails.
"""
import json
import os
import re
import sys
from collections import defaultdict

# Repo copy: the downloadable packs live in packs/ beside tools/.
# (The workspace copy of this script defaults to the pipeline
# masters instead; the rules are identical.)
_PACKS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "packs")
DEFAULTS = [
    os.path.join(_PACKS_DIR, "episodes.json"),
    os.path.join(_PACKS_DIR, "episodes-csi.json"),
]

LINK_ID_FORMS = {
    "prime": re.compile(r"^amzn1\.dv\.gti\.[0-9a-fA-F-]{36}$"),
    "netflix": re.compile(r"^\d{5,12}$"),
    "disney": re.compile(
        r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-"
        r"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"),
}
CATEGORIES = {"film", "episode", "special", "one-shot", "web"}


def validate(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    episodes = data["episodes"]
    problems = []
    warnings = []

    # --- order sanity: exactly 1..N, gapless ---
    orders = sorted(ep.get("order") for ep in episodes)
    if orders != list(range(1, len(episodes) + 1)):
        problems.append(
            f"order values are not exactly 1..{len(episodes)} "
            f"(gapless, unique)")

    # --- keys: unique and consistent with show/season/episode ---
    seen_keys = set()
    for ep in episodes:
        key = ep.get("key")
        if key in seen_keys:
            problems.append(f"duplicate key {key!r}")
        seen_keys.add(key)
        expect = f"{ep.get('show')}|{ep.get('season')}|{ep.get('episodeNumber')}"
        if key != expect:
            problems.append(f"key {key!r} != expected {expect!r}")

    # --- links: platform forms, one per platform, no dup IDs ---
    seen_links = {}
    n_links = 0
    for ep in episodes:
        effective = list(ep.get("links") or [])
        if ep.get("primeContentId"):
            effective.append({"platform": "prime",
                                "id": ep["primeContentId"]})
        per_platform = defaultdict(int)
        for link in effective:
            platform, lid = link.get("platform"), link.get("id") or ""
            per_platform[platform] += 1
            n_links += 1
            form = LINK_ID_FORMS.get(platform)
            if form is None:
                problems.append(
                    f"{ep['key']}: unknown link platform {platform!r}")
            elif not form.match(lid):
                problems.append(
                    f"{ep['key']}: {platform} link ID {lid!r} does not "
                    f"match the platform's ID form")
            pair = (platform, lid)
            if pair in seen_links:
                problems.append(
                    f"duplicate {platform} ID {lid!r} on {ep['key']} "
                    f"and {seen_links[pair]}")
            seen_links[pair] = ep["key"]
        for platform, count in per_platform.items():
            if count > 1:
                problems.append(
                    f"{ep['key']}: {count} links for platform "
                    f"{platform!r} (max one)")

    # --- categories ---
    for ep in episodes:
        cat = ep.get("category")
        if cat is not None and cat not in CATEGORIES:
            problems.append(f"{ep['key']}: unknown category {cat!r}")

    # --- runtimes: warning only ---
    no_runtime = [ep["key"] for ep in episodes
                  if not ep.get("runtimeMinutes")]
    if no_runtime:
        warnings.append(
            f"{len(no_runtime)} entries have no runtimeMinutes "
            f"(first: {', '.join(no_runtime[:5])})")

    # --- event guards (the original regression checks) ---
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
    print(f"{status}  {path}: {len(episodes)} episodes, {n_links} links, "
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
