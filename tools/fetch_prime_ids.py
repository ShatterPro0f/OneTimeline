#!/usr/bin/env python3
"""Batch-fill primeContentId and runtimeMinutes in episodes.json from
JustWatch (Canada).

Sibling to build_episodes.py: run build_episodes.py first (it regenerates
episodes.json with primeContentId="" and TVmaze's flat runtime), then run
this to enrich it. Re-runnable — episodes that already carry an ID are
left untouched unless --force is given.

Runtimes (iteration 5): the same episode query also returns JustWatch's
per-episode `runtime` — the true streamed length (Med S1 runs 41-43 min),
not TVmaze's 60-minute broadcast slot. Every row's runtimeMinutes is
(re)written from, in order: the episode's own JustWatch runtime; the
median runtime of its show+season; the median of its show; else 60.
Coverage is reported per tier in runtime-report.txt.

Source: JustWatch's public GraphQL API (https://apis.justwatch.com/graphql,
no key, country=CA), per id-automation-findings.md. JustWatch returns
episode-specific Prime links whose identifier is a GTI
(amzn1.dv.gti.…), never a B0 ASIN. Matching is by (show node, season,
episode number) with the episode title as a sanity check; JustWatch
sometimes appends crossover part markers ("(I)", "(III)") to titles —
those are stripped before comparing.

Expected holes (Canada): all of Chicago Justice (no Prime listing),
the newest 1-2 episodes of currently-airing seasons (offer lag).
"""
import json
import re
import sys
import time
import urllib.request

JW_URL = "https://apis.justwatch.com/graphql"
JW_SHOW_NODES = {
    "Fire": "ts3630",
    "PD": "ts3447",
    "Med": "ts37490",
    "Law & Order: SVU": "ts20386",
    "Justice": "ts47931",
    "FBI": "ts82308",
}

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
          package { clearName }
        }
        content(country: "CA", language: "en") { title episodeNumber runtime }
      }
    }
  }
}
"""

GTI_RE = re.compile(r"(amzn1\.dv\.gti\.[0-9a-fA-F-]{36})")
PART_SUFFIX_RE = re.compile(r"\s*\((?:I|II|III|IV|V)\)\s*$")


def gql(query, variables):
    body = json.dumps({"query": query, "variables": variables}).encode()
    req = urllib.request.Request(
        JW_URL, data=body,
        headers={"Content-Type": "application/json",
                 "User-Agent": "Mozilla/5.0 (OneChicago tracker data pipeline)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        out = json.loads(r.read().decode("utf-8"))
    if out.get("errors"):
        raise RuntimeError(f"GraphQL errors: {out['errors']}")
    return out["data"]


def extract_gti(offers):
    """GTI from the first Amazon offer URL; falls back to any URL with gti=."""
    fallback = None
    for offer in offers or []:
        url = offer.get("standardWebURL") or ""
        m = GTI_RE.search(url)
        if not m:
            continue
        if "primevideo" in url or "amazon" in url:
            return m.group(1)
        if fallback is None:
            fallback = m.group(1)
    return fallback


def norm_title(t):
    return PART_SUFFIX_RE.sub("", (t or "").strip()).strip().lower()


def main():
    force = "--force" in sys.argv
    path = "episodes.json"
    with open(path, encoding="utf-8") as f:
        rows = json.load(f)

    # Group dataset episodes by (show, season) — one GraphQL call per season.
    wanted = {}
    for row in rows:
        wanted.setdefault((row["show"], row["season"]), []).append(row)

    jw_cache = {}  # (show, seasonNumber) -> {episodeNumber: (gti, jwTitle, runtime|None)}
    warnings = []
    fetched_seasons = 0

    for show, node_id in JW_SHOW_NODES.items():
        seasons_needed = sorted(s for (sh, s) in wanted if sh == show)
        if not seasons_needed:
            continue
        data = gql(SEASONS_QUERY, {"id": node_id})
        season_nodes = {}
        for s in (data["node"] or {}).get("seasons") or []:
            sn = (s.get("content") or {}).get("seasonNumber")
            if sn is not None:
                season_nodes[sn] = s["id"]
        time.sleep(0.4)
        for season in seasons_needed:
            sid = season_nodes.get(season)
            if sid is None:
                warnings.append(f"{show} S{season}: season not found on JustWatch")
                continue
            data = gql(EPISODES_QUERY, {"id": sid})
            fetched_seasons += 1
            eps = {}
            for ep in (data["node"] or {}).get("episodes") or []:
                content = ep.get("content") or {}
                num = content.get("episodeNumber")
                if num is None:
                    continue
                rt = content.get("runtime")
                eps[num] = (extract_gti(ep.get("offers")), content.get("title") or "",
                            rt if isinstance(rt, (int, float)) and rt > 0 else None)
            jw_cache[(show, season)] = eps
            time.sleep(0.4)
        print(f"justwatch {show}: seasons fetched", file=sys.stderr)

    filled = 0
    already = 0
    missing = []
    for row in rows:
        if row.get("primeContentId") and not force:
            already += 1
            continue
        eps = jw_cache.get((row["show"], row["season"]))
        hit = eps.get(row["episodeNumber"]) if eps else None
        if hit and hit[0]:
            gti, jw_title = hit[0], hit[1]
            row["primeContentId"] = gti
            filled += 1
            if norm_title(jw_title) != norm_title(row["title"]):
                warnings.append(
                    f"title mismatch order {row['order']} {row['show']} {row['code']}: "
                    f"dataset {row['title']!r} vs JustWatch {jw_title!r} (ID written anyway)")
        else:
            missing.append(row)

    # ---- Runtime pass (iteration 5): true streamed lengths ------------
    # Runs over EVERY row (independent of ID status). Tier 1: the
    # episode's own JustWatch runtime. Tier 2: median of its show+season.
    # Tier 3: median of its show. Tier 4: 60 (the old flat default).
    import statistics

    def median_int(values):
        return int(round(statistics.median(values))) if values else None

    season_known = {}
    show_known = {}
    for row in rows:
        eps = jw_cache.get((row["show"], row["season"]))
        hit = eps.get(row["episodeNumber"]) if eps else None
        row["_jwRuntime"] = hit[2] if hit else None
        if row["_jwRuntime"]:
            season_known.setdefault((row["show"], row["season"]), []).append(row["_jwRuntime"])
            show_known.setdefault(row["show"], []).append(row["_jwRuntime"])
    season_median = {k: median_int(v) for k, v in season_known.items()}
    show_median = {k: median_int(v) for k, v in show_known.items()}

    tiers = {"true": 0, "seasonMedian": 0, "showMedian": 0, "flat60": 0}
    tier_rows = {"seasonMedian": [], "showMedian": [], "flat60": []}
    for row in rows:
        rt = row.pop("_jwRuntime")
        if rt:
            row["runtimeMinutes"] = int(round(rt))
            tiers["true"] += 1
        elif season_median.get((row["show"], row["season"])):
            row["runtimeMinutes"] = season_median[(row["show"], row["season"])]
            tiers["seasonMedian"] += 1
            tier_rows["seasonMedian"].append(row)
        elif show_median.get(row["show"]):
            row["runtimeMinutes"] = show_median[row["show"]]
            tiers["showMedian"] += 1
            tier_rows["showMedian"].append(row)
        else:
            row["runtimeMinutes"] = 60
            tiers["flat60"] += 1
            tier_rows["flat60"].append(row)

    with open(path, "w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=1)

    total_with = sum(1 for r in rows if r.get("primeContentId"))
    print(f"seasons fetched: {fetched_seasons}")
    print(f"total episodes: {len(rows)}")
    print(f"newly filled: {filled}, already had ID: {already}")
    print(f"TOTAL with primeContentId: {total_with}")
    print(f"missing: {len(missing)}")
    for r in missing:
        print(f"  MISSING order {r['order']}: {r['show']} {r['code']} {r['title']!r}")
    for w in warnings:
        print(f"  WARNING: {w}")

    with open("prime-id-report.txt", "w", encoding="utf-8") as f:
        f.write(f"total={len(rows)} withId={total_with} missing={len(missing)} "
                f"newlyFilled={filled} seasonsFetched={fetched_seasons}\n")
        for r in missing:
            f.write(f"MISSING order {r['order']}: {r['show']} {r['code']} {r['title']!r}\n")
        for w in warnings:
            f.write(f"WARNING: {w}\n")
    e125 = rows[124]
    print(f"order125: {e125['show']} {e125['code']} {e125['title']!r} id={e125.get('primeContentId')!r}")

    print(f"runtimes: true={tiers['true']} seasonMedian={tiers['seasonMedian']} "
          f"showMedian={tiers['showMedian']} flat60={tiers['flat60']}")
    for tier in ("seasonMedian", "showMedian", "flat60"):
        for r in tier_rows[tier]:
            print(f"  RUNTIME-{tier} order {r['order']}: {r['show']} {r['code']} {r['title']!r} -> {r['runtimeMinutes']}")
    spots = []
    for show, s, e in (("Med", 1, 1), ("Fire", 1, 1), ("PD", 1, 1)):
        row = next((r for r in rows if r["show"] == show and r["season"] == s
                    and r["episodeNumber"] == e), None)
        if row:
            spots.append(f"{show} {row['code']} {row['title']!r} runtime={row['runtimeMinutes']}")
            print(f"spot: {spots[-1]}")
    with open("runtime-report.txt", "w", encoding="utf-8") as f:
        f.write(f"total={len(rows)} true={tiers['true']} seasonMedian={tiers['seasonMedian']} "
                f"showMedian={tiers['showMedian']} flat60={tiers['flat60']}\n")
        for s in spots:
            f.write(f"SPOT {s}\n")
        for tier in ("seasonMedian", "showMedian", "flat60"):
            for r in tier_rows[tier]:
                f.write(f"{tier} order {r['order']}: {r['show']} {r['code']} {r['title']!r} -> {r['runtimeMinutes']}\n")


if __name__ == "__main__":
    main()
