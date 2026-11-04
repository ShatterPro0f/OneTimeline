#!/usr/bin/env python3
"""Fill primeContentId and runtimeMinutes in episodes-csi.json from
JustWatch (Canada), for the One Timeline CSI pack.

Sibling to build_csi_episodes.py (run it first) and modelled on
fetch_prime_ids.py (the Chicago pass). Re-runnable; --cache reuses
the raw JustWatch responses cached in csi-work/ instead of fetching.

What counts as a Prime link here (csi-research.md §3): NOTHING in
this franchise streams on base Prime Video in Canada. The IDs this
script writes are **Paramount+ Amazon Channel** GTIs — genuine Prime
Video content IDs (amzn1.dv.gti.…) for episodes that open inside
Prime Video with the Paramount+ channel add-on. Only FLATRATE
offers from the Paramount+ Amazon Channel (or base Prime, should
any ever appear) are accepted; purchase listings never yield an ID.

Runtimes are the episodes' true streamed lengths from the same
JustWatch records. Two documented corrections:
  - "Grave Danger" (CSI S5E24/E25): JustWatch stamps the combined
    two-hour runtime (85 min) on BOTH part records. The parts are
    separate episodes with separate IDs, so the combined figure is
    split across them: 43 + 42 = 85.
  - "Immortality" (CSI S16E1/E2): JustWatch files the finale outside
    its season structure and carries no record for it at all. The
    film's documented length is 88 minutes (Wikipedia), so each
    part carries 44.
"""
import json
import re
import sys
import time
import urllib.request
from collections import Counter

JW_URL = "https://apis.justwatch.com/graphql"
WORK_DIR = "csi-work"
JW_SHOW_NODES = {
    "CSI": "ts18517",
    "Miami": "ts18569",
    "NY": "ts17931",
    "Cyber": "ts18418",
    "Vegas": "ts285960",
    "WAT": "ts22050",
}
# Only these seasons are fetched per show (WAT: the cameo's season).
SEASONS_WANTED = {"WAT": {6}}

ACCEPTED_PACKAGES = {"Paramount+ Amazon Channel", "Amazon Prime Video"}

SEASONS_QUERY = """
query GetSeasons($id: ID!) {
  node(id: $id) {
    ... on Show {
      seasons {
        id
        content(country: "CA", language: "en") { seasonNumber }
      }
    }
  }
}
"""

EPISODES_QUERY = """
query GetEpisodes($id: ID!) {
  node(id: $id) {
    ... on Season {
      episodes {
        offers(country: "CA", platform: WEB) {
          standardWebURL
          monetizationType
          package { clearName }
        }
        content(country: "CA", language: "en") { title episodeNumber runtime }
      }
    }
  }
}
"""

GTI_RE = re.compile(r"(amzn1\.dv\.gti\.[0-9a-fA-F-]{36})")
PART_SUFFIX_RE = re.compile(r"\s*\((?:I|II|III|IV|V|\d+)\)\s*$")


def gql(query, variables):
    body = json.dumps({"query": query, "variables": variables}).encode()
    req = urllib.request.Request(
        JW_URL, data=body,
        headers={"Content-Type": "application/json",
                 "User-Agent": "Mozilla/5.0 (OneTimeline CSI pack data pipeline)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        out = json.loads(r.read().decode("utf-8"))
    if out.get("errors"):
        raise RuntimeError(f"GraphQL errors: {out['errors']}")
    return out["data"]


def fetch_show(node_id, seasons_wanted=None):
    """-> {seasonNumber: [episode records]}; cached to csi-work/."""
    cache_path = f"{WORK_DIR}/jw-{node_id}.json"
    if "--cache" in sys.argv:
        with open(cache_path, encoding="utf-8") as f:
            raw = json.load(f)
        return {int(k): v for k, v in raw.items()}
    data = gql(SEASONS_QUERY, {"id": node_id})
    season_nodes = {}
    for s in (data["node"] or {}).get("seasons") or []:
        sn = (s.get("content") or {}).get("seasonNumber")
        if sn is not None:
            season_nodes[sn] = s["id"]
    time.sleep(0.4)
    out = {}
    for sn in sorted(season_nodes):
        if seasons_wanted and sn not in seasons_wanted:
            continue
        data = gql(EPISODES_QUERY, {"id": season_nodes[sn]})
        out[sn] = (data["node"] or {}).get("episodes") or []
        time.sleep(0.4)
    if not seasons_wanted:
        with open(cache_path, "w", encoding="utf-8") as f:
            json.dump({str(k): v for k, v in out.items()}, f)
    return out


def accepted_gti(offers):
    """(gti, package) from the first accepted FLATRATE Prime offer."""
    for offer in offers or []:
        pkg = ((offer.get("package") or {}).get("clearName") or "")
        if offer.get("monetizationType") != "FLATRATE" or pkg not in ACCEPTED_PACKAGES:
            continue
        m = GTI_RE.search(offer.get("standardWebURL") or "")
        if m:
            return m.group(1), pkg
    return None, None


def norm_title(t):
    return PART_SUFFIX_RE.sub("", (t or "").strip()).strip().lower()


# Documented runtime corrections (see module docstring).
RUNTIME_OVERRIDES = {
    "CSI|5|24": 43,   # Grave Danger (1): JW's combined 85 split 43+42
    "CSI|5|25": 42,   # Grave Danger (2)
    "CSI|16|1": 44,   # Immortality (1): 88-minute film, two catalogue parts
    "CSI|16|2": 44,   # Immortality (2)
}


def main():
    path = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") \
        else "episodes-csi.json"
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    rows = data["episodes"]

    wanted = {}
    for row in rows:
        wanted.setdefault(row["show"], set()).add(row["season"])

    jw = {}  # (show, season, episodeNumber) -> (gti, package, jwTitle, runtime)
    for show, node_id in JW_SHOW_NODES.items():
        if show not in wanted:
            continue
        seasons = fetch_show(node_id, SEASONS_WANTED.get(show))
        for sn, eps in seasons.items():
            if sn not in wanted[show]:
                continue
            for ep in eps:
                content = ep.get("content") or {}
                num = content.get("episodeNumber")
                if num is None:
                    continue
                gti, pkg = accepted_gti(ep.get("offers"))
                rt = content.get("runtime")
                jw[(show, sn, num)] = (
                    gti, pkg, content.get("title") or "",
                    int(rt) if isinstance(rt, (int, float)) and rt > 0 else None)
        print(f"justwatch {show}: fetched", file=sys.stderr)

    warnings = []
    missing_id, missing_rt = [], []
    per_show = {}
    for row in rows:
        st = per_show.setdefault(row["show"], Counter())
        hit = jw.get((row["show"], row["season"], row["episodeNumber"]))
        gti = hit[0] if hit else None
        if gti:
            row["primeContentId"] = gti
            st["withId"] += 1
            st[f"idVia:{hit[1]}"] += 1
            if norm_title(hit[2]) != norm_title(row["title"]):
                warnings.append(
                    f"title mismatch order {row['order']} {row['key']}: "
                    f"dataset {row['title']!r} vs JustWatch {hit[2]!r} (ID written anyway)")
        else:
            missing_id.append(row)
        if row["key"] in RUNTIME_OVERRIDES:
            row["runtimeMinutes"] = RUNTIME_OVERRIDES[row["key"]]
            st["runtimeCorrected"] += 1
        elif hit and hit[3]:
            row["runtimeMinutes"] = hit[3]
            st["runtimeTrue"] += 1
        else:
            missing_rt.append(row)
        st["total"] += 1

    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)

    total_with = sum(1 for r in rows if r.get("primeContentId"))
    total_rt = sum(1 for r in rows if r.get("runtimeMinutes"))
    print(f"total episodes: {len(rows)}")
    print(f"TOTAL with primeContentId: {total_with}; without: {len(missing_id)}")
    print(f"TOTAL with runtimeMinutes: {total_rt}; without: {len(missing_rt)}")
    for show, st in per_show.items():
        print(f"  {show}: {dict(st)}")
    for r in missing_id:
        print(f"  NO-ID order {r['order']}: {r['key']} {r['title']!r}")
    for r in missing_rt:
        print(f"  NO-RUNTIME order {r['order']}: {r['key']} {r['title']!r}")
    for w in warnings:
        print(f"  WARNING: {w}")

    with open("csi-prime-id-report.txt", "w", encoding="utf-8") as f:
        f.write(f"total={len(rows)} withId={total_with} missing={len(missing_id)} "
                f"withRuntime={total_rt} missingRuntime={len(missing_rt)}\n")
        for show, st in per_show.items():
            f.write(f"{show}: {dict(st)}\n")
        for r in missing_id:
            f.write(f"NO-ID order {r['order']}: {r['key']} {r['title']!r}\n")
        for r in missing_rt:
            f.write(f"NO-RUNTIME order {r['order']}: {r['key']} {r['title']!r}\n")
        for w in warnings:
            f.write(f"WARNING: {w}\n")


if __name__ == "__main__":
    main()
