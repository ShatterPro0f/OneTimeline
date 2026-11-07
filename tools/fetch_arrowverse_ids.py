#!/usr/bin/env python3
"""Fetch JustWatch (Canada) episode offers for the Arrowverse pack.

Sibling to fetch_prime_ids.py (the One Chicago pipeline), extended for
what this pack needs beyond a GTI:

  * offers are classified by monetizationType — only FLATRATE (or ADS,
    i.e. the ad-supported tier of a subscription service) offers count
    as "streams in Canada"; BUY/RENT offers are purchase pages and
    their IDs are NEVER used as links (the rule the §1H.6 amendment
    restates, and the reason The Flash's Prime IDs stay out).
  * every package seen per show is recorded, so the run doubles as the
    Canada-availability re-verification the stage-A report needs.
  * JustWatch's per-episode `runtime` (the true streamed length) is
    captured for the pack's runtimeMinutes — the same source the One
    Chicago pipeline switched to (its iteration 5), because TVmaze's
    per-episode runtime is the broadcast slot, not the content length.

Source: JustWatch's public GraphQL API (https://apis.justwatch.com/graphql,
no key, country=CA). Show node IDs are the ones recovered in
arrowverse-research.md (2026-11-03) from JustWatch's own show pages.

Output: arrowverse-work/jw-arrowverse.json —
  { "<show>": { "node": "ts…",
                "seasons": { "<seasonNumber>": [ {episodeNumber, title,
                    runtime, offers: [{package, monetizationType, url,
                    gti}] } ] } } }
Raw per-show responses are cached under arrowverse-work/raw/ and the
fetch is skipped for any show whose cache file exists (delete to
re-run).
"""
import json
import os
import re
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
OUT = os.path.join(HERE, "jw-arrowverse.json")
JW_URL = "https://apis.justwatch.com/graphql"

JW_SHOW_NODES = {
    "Arrow": "ts17598",
    "The Flash": "ts11188",
    "Supergirl": "ts86477",
    "DC's Legends of Tomorrow": "ts36550",
    "Batwoman": "ts90742",
    "Black Lightning": "ts57055",
    "Constantine": "ts12730",
    "Superman & Lois": "ts222614",
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
          monetizationType
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


def gql(query, variables):
    body = json.dumps({"query": query, "variables": variables}).encode()
    req = urllib.request.Request(
        JW_URL, data=body,
        headers={"Content-Type": "application/json",
                 "User-Agent": "Mozilla/5.0 (OneTimeline tracker data pipeline)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        out = json.loads(r.read().decode("utf-8"))
    if out.get("errors"):
        raise RuntimeError(f"GraphQL errors: {out['errors']}")
    return out["data"]


def fetch_show(show, node_id):
    cache = os.path.join(RAW, f"jw-{node_id}.json")
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            return json.load(f)
    data = gql(SEASONS_QUERY, {"id": node_id})
    season_nodes = {}
    for s in (data["node"] or {}).get("seasons") or []:
        sn = (s.get("content") or {}).get("seasonNumber")
        if sn is not None:
            season_nodes[sn] = s["id"]
    time.sleep(0.4)
    seasons = {}
    for sn in sorted(season_nodes):
        data = gql(EPISODES_QUERY, {"id": season_nodes[sn]})
        eps = []
        for ep in (data["node"] or {}).get("episodes") or []:
            content = ep.get("content") or {}
            offers = []
            for offer in ep.get("offers") or []:
                url = offer.get("standardWebURL") or ""
                m = GTI_RE.search(url)
                offers.append({
                    "package": (offer.get("package") or {}).get("clearName"),
                    "monetizationType": offer.get("monetizationType"),
                    "url": url,
                    "gti": m.group(1) if m else None,
                })
            rt = content.get("runtime")
            eps.append({
                "episodeNumber": content.get("episodeNumber"),
                "title": content.get("title") or "",
                "runtime": rt if isinstance(rt, (int, float)) and rt > 0 else None,
                "offers": offers,
            })
        seasons[str(sn)] = eps
        print(f"  {show} S{sn}: {len(eps)} episodes", file=sys.stderr)
        time.sleep(0.4)
    result = {"node": node_id, "seasons": seasons}
    with open(cache, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    return result


def main():
    out = {}
    for show, node_id in JW_SHOW_NODES.items():
        print(f"justwatch: {show} ({node_id})", file=sys.stderr)
        out[show] = fetch_show(show, node_id)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
