#!/usr/bin/env python3
"""Build the Arrowverse universe pack (episodes-arrowverse.json).

Pipeline (mirrors build_mcu_episodes.py / build_csi_episodes.py):

  1. ORDER — arrowverse.info's "Arrowverse Episode Order" (the pack's
     named order document; snapshot cached in arrowverse-work/raw/).
     830 rows; the pack keeps the ruled scope (§1H.6): the 8
     live-action series incl. Constantine and Superman & Lois;
     Stargirl, Vixen and Freedom Fighters: The Ray are out.
     830 - 41 - 12 - 12 = 765 episodes. The build asserts this total
     and the per-show counts rather than assuming them.
  2. IDENTITY / DESCRIPTIONS / AIR DATES — TVmaze (CC BY-SA), joined
     on (season, episodeNumber); TVmaze's title wins where the two
     sources differ (the standing rule from the earlier packs).
     One reconciliation: arrowverse.info lists "Crisis on Infinite
     Earths: Part Five" as a Legends special, "S05E0—", and numbers
     the season's regular run S5E1–E14 from there; TVmaze keys the
     Crisis episode as a regular S5E1 and the run as S5E2–E15. The
     pack keys everything TVmaze's way — the app's identity format
     requires episodeNumber >= 1, and the app's TVmaze refresh joins
     on this key — so the whole Legends S5 block shifts by one
     (order-source S5E0 -> pack 5|1, S5E1 -> 5|2, …, S5E14 -> 5|15),
     preserving the order source's sequence exactly. The build
     asserts the shifted rows' titles agree between the sources.
  3. RUNTIMES — JustWatch Canada's per-episode true streamed lengths
     (fetched by fetch_arrowverse_ids.py), the same source the One
     Chicago pipeline uses: TVmaze's per-episode runtime is the
     broadcast slot, not the content length, and the app judges
     episode endings against this number.
  4. LINKS (stage A) — a Prime link is written ONLY from a JustWatch
     CA offer whose monetizationType is FLATRATE/ADS on a Prime
     package (a genuine streaming listing). BUY/RENT offers are
     purchase pages; their IDs are never used (that is why The
     Flash — Netflix in Canada, purchase-only on Prime — ships
     linkless in stage A, and why Arrow S4E8, buy-only on JustWatch
     CA, is the one Arrow episode without a link).
  5. EVENTS / CAMEOS — from the crossover map in
     arrowverse-research.md §4, under the standing rule: event
     groups only for stories told across 2+ episodes; single-episode
     appearances (Worlds Finest, Constantine's "Haunted") are cameos.

Output: arrowverse-work/episodes-arrowverse.json (the pack envelope)
+ arrowverse-work/build-notes.json (coverage and audit detail).
"""
import html as html_lib
import json
import os
import re
import sys
import time
import urllib.request
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
ORDER_HTML = os.path.join(RAW, "arrowverse-info.html")
JW_FILE = os.path.join(HERE, "jw-arrowverse.json")
OUT = os.path.join(HERE, "episodes-arrowverse.json")
NOTES = os.path.join(HERE, "build-notes.json")

TVMAZE_IDS = {
    "Arrow": 4,
    "The Flash": 13,
    "Supergirl": 1850,
    "DC's Legends of Tomorrow": 1851,
    "Batwoman": 37776,
    "Black Lightning": 20683,
    "Constantine": 15,
    "Superman & Lois": 44751,
}
OUT_OF_SCOPE = {"Stargirl", "Vixen", "Freedom Fighters: The Ray"}
EXPECTED_COUNTS = {
    "Arrow": 170, "The Flash": 184, "Supergirl": 126,
    "DC's Legends of Tomorrow": 110, "Batwoman": 51,
    "Black Lightning": 58, "Constantine": 13, "Superman & Lois": 53,
}

# (group id, eventKind, [(show, season, episode), ...] in story order)
EVENTS = [
    ("flash-vs-arrow", "crossover",
     [("The Flash", 1, 8), ("Arrow", 3, 8)]),
    ("heroes-join-forces", "crossover",
     [("The Flash", 2, 8), ("Arrow", 4, 8)]),
    ("invasion", "crossover",
     [("Supergirl", 2, 8), ("The Flash", 3, 8), ("Arrow", 5, 8),
      ("DC's Legends of Tomorrow", 2, 7)]),
    ("duet", "crossover",
     [("Supergirl", 2, 16), ("The Flash", 3, 17)]),
    ("crisis-on-earth-x", "crossover",
     [("Supergirl", 3, 8), ("Arrow", 6, 8), ("The Flash", 4, 8),
      ("DC's Legends of Tomorrow", 3, 8)]),
    ("elseworlds", "crossover",
     [("The Flash", 5, 9), ("Arrow", 7, 9), ("Supergirl", 4, 9)]),
    ("crisis-on-infinite-earths", "crossover",
     [("Supergirl", 5, 9), ("Batwoman", 1, 9), ("Black Lightning", 3, 9),
      ("The Flash", 6, 9), ("Arrow", 8, 8),
      ("DC's Legends of Tomorrow", 5, 1)]),
    ("armageddon", "multi-part",
     [("The Flash", 8, 1), ("The Flash", 8, 2), ("The Flash", 8, 3),
      ("The Flash", 8, 4), ("The Flash", 8, 5)]),
]

CAMEOS = {
    ("Supergirl", 1, 18): [{"show": "The Flash",
                            "names": "Barry Allen / The Flash"}],
    ("Arrow", 4, 5): [{"show": "Constantine",
                       "names": "John Constantine"}],
}

GTI_RE = re.compile(r"^amzn1\.dv\.gti\.[0-9a-fA-F-]{36}$")
CODE_RE = re.compile(r"^S(\d+)E(\d+)$")


def strip_tags(text):
    if not text:
        return ""
    text = re.sub(r"<[^>]+>", " ", text)
    return re.sub(r"\s+", " ", html_lib.unescape(text)).strip()


def fetch_tvmaze(show, show_id):
    cache = os.path.join(RAW, f"tvmaze-{show_id}.json")
    if os.path.exists(cache):
        with open(cache, encoding="utf-8") as f:
            return json.load(f)
    url = f"https://api.tvmaze.com/shows/{show_id}/episodes?specials=1"
    req = urllib.request.Request(
        url, headers={"User-Agent": "OneTimeline data pipeline"})
    with urllib.request.urlopen(req, timeout=60) as r:
        data = json.loads(r.read().decode("utf-8"))
    with open(cache, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    time.sleep(0.3)
    return data


def pack_ident(show, season, episode, special_code):
    """Translate an order-source (season, episode) to the pack's
    TVmaze-keyed identity. Only one block in the franchise needs it:
    Legends S5, where the order source counts the Crisis special as
    E0 and the regular run from E1, while TVmaze counts the Crisis
    episode as E1 and the run from E2."""
    if show == "DC's Legends of Tomorrow" and season == 5:
        if special_code:
            return (5, 1)
        return (5, episode + 1)
    return (season, episode)


def parse_order():
    with open(ORDER_HTML, encoding="utf-8") as f:
        page = f.read()
    rows = []
    for body in re.findall(r'<tr class="episode [a-z-]+">(.*?)</tr>',
                           page, re.S):
        tds = re.findall(r"<td>(.*?)</td>", body, re.S)
        cells = [html_lib.unescape(re.sub(r"<[^>]+>", "", t).strip())
                 for t in tds]
        pos = int(cells[0])
        series, code, title = cells[1], cells[2], cells[3]
        m = CODE_RE.match(code)
        special = None
        if not m:
            # The one irregular row: Legends "S05E0—" (Crisis Part Five),
            # a special in the order source, regular S5E1 on TVmaze.
            special = code
            season, episode = 5, 0
        else:
            season, episode = int(m.group(1)), int(m.group(2))
        p_season, p_episode = pack_ident(series, season, episode, special)
        rows.append({"sheetNumber": pos, "show": series, "season": p_season,
                     "episodeNumber": p_episode, "sourceTitle": title,
                     "sourceCode": code, "specialCode": special,
                     "sourceSeason": season, "sourceEpisode": episode})
    return rows


def main():
    notes = {"titleMismatches": [], "warnings": []}

    # ---- 1. order -------------------------------------------------
    all_rows = parse_order()
    assert len(all_rows) == 830, f"order rows {len(all_rows)} != 830"
    rows = [r for r in all_rows if r["show"] not in OUT_OF_SCOPE]
    counts = Counter(r["show"] for r in rows)
    assert dict(counts) == EXPECTED_COUNTS, \
        f"per-show counts {dict(counts)} != expected {EXPECTED_COUNTS}"
    assert len(rows) == 765, f"in-scope rows {len(rows)} != 765"
    notes["orderRows"] = len(all_rows)
    notes["perShowCounts"] = dict(counts)

    # ---- 2. TVmaze ------------------------------------------------
    tvmaze = {}   # show -> {(season, ep): episode dict}
    for show, sid in TVMAZE_IDS.items():
        eps = {}
        for ep in fetch_tvmaze(show, sid):
            if ep.get("type") != "regular":
                continue
            eps[(ep["season"], ep["number"])] = ep
        assert len(eps) == EXPECTED_COUNTS[show], \
            f"TVmaze regular count for {show}: {len(eps)}"
        tvmaze[show] = eps

    # ---- 3+4. JustWatch: runtimes, links, availability ------------
    with open(JW_FILE, encoding="utf-8") as f:
        jw = json.load(f)
    jw_eps = {}   # show -> {(season, ep): jw episode}
    canada = {}
    for show, data in jw.items():
        per = {}
        flat_pkgs = Counter()
        buy_pkgs = Counter()
        prime_linked = 0
        for sn, eps in data["seasons"].items():
            for ep in eps:
                key = (int(sn), ep["episodeNumber"])
                per[key] = ep
                seen_flat, seen_buy = set(), set()
                for o in ep["offers"]:
                    if o["monetizationType"] in ("FLATRATE", "ADS"):
                        seen_flat.add(o["package"])
                    elif o["monetizationType"] in ("BUY", "RENT"):
                        seen_buy.add(o["package"])
                for p in seen_flat:
                    flat_pkgs[p] += 1
                for p in seen_buy:
                    buy_pkgs[p] += 1
                if streaming_prime_gti(ep):
                    prime_linked += 1
        jw_eps[show] = per
        canada[show] = {
            "episodes": sum(EXPECTED_COUNTS[show] for _ in [0]),
            "flatratePackages": dict(flat_pkgs),
            "buyOnlyPackages": dict(buy_pkgs),
            "primeStreamingLinked": prime_linked,
        }
    notes["canada"] = canada

    def _unused():
        pass

    # ---- join ------------------------------------------------------
    episodes = []
    runtime_missing = []
    desc_missing = []
    tvmaze_runtime_agree = 0
    tvmaze_runtime_total = 0
    for i, r in enumerate(rows, start=1):
        show = r["show"]
        ident = (r["season"], r["episodeNumber"])
        tv = tvmaze[show].get(ident)
        assert tv is not None, f"no TVmaze episode for {show} {ident}"
        jw_ep = jw_eps[show].get(ident)
        assert jw_ep is not None, f"no JustWatch episode for {show} {ident}"

        title = tv.get("name") or r["sourceTitle"]
        if show == "DC's Legends of Tomorrow" and r["season"] == 5:
            # The shifted block: the two sources must be talking about
            # the same episode — titles agree (case aside) or the
            # shift is wrong and the build stops here.
            assert title.lower() == r["sourceTitle"].lower(), \
                f"Legends S5 shift mismatch: order {r['sourceTitle']!r} " \
                f"vs TVmaze {title!r}"
        if title != r["sourceTitle"]:
            notes["titleMismatches"].append(
                f"{show} {ident}: order {r['sourceTitle']!r} "
                f"-> TVmaze {title!r}")
        desc = strip_tags(tv.get("summary"))
        if not desc:
            desc_missing.append(f"{show}|{ident[0]}|{ident[1]}")

        runtime = jw_ep["runtime"]
        if runtime:
            runtime = int(round(runtime))
        else:
            runtime_missing.append(f"{show}|{ident[0]}|{ident[1]}")
        if tv.get("runtime"):
            tvmaze_runtime_total += 1
            if runtime and tv["runtime"] == runtime:
                tvmaze_runtime_agree += 1

        links = []
        gti = streaming_prime_gti(jw_ep)
        if gti:
            links.append({"platform": "prime", "id": gti})

        episodes.append({
            "order": i,
            "sheetNumber": r["sheetNumber"],
            "key": f"{show}|{r['season']}|{r['episodeNumber']}",
            "show": show,
            "season": r["season"],
            "episodeNumber": r["episodeNumber"],
            "code": f"{r['season']}x{r['episodeNumber']:02d}",
            "title": title,
            "airDate": tv.get("airdate") or None,
            "description": desc,
            "runtimeMinutes": runtime,
            "cameos": CAMEOS.get((show, r["season"], r["episodeNumber"]),
                                 []),
            "eventKind": None,
            "eventIndex": None,
            "eventTotal": None,
            "eventGroup": None,
            "links": links,
        })
        if r["specialCode"]:
            notes["crisisPartFive"] = {
                "orderSourceCode": r["specialCode"],
                "orderSourcePosition": r["sheetNumber"],
                "packKey": episodes[-1]["key"],
                "order": i,
                "title": title,
                "blockShift": ("the whole Legends S5 block is keyed "
                               "TVmaze's way: order-source S5E0 -> 5|1, "
                               "S5E1 -> 5|2, ..., S5E14 -> 5|15"),
            }
    notes["runtimeMissing"] = runtime_missing
    notes["descriptionMissing"] = desc_missing
    notes["tvmazeRuntimePresent"] = tvmaze_runtime_total
    notes["tvmazeRuntimeAgreesWithJw"] = tvmaze_runtime_agree

    # every TVmaze regular episode was consumed exactly once
    used = Counter((ep["show"], ep["season"], ep["episodeNumber"])
                   for ep in episodes)
    for show, eps in tvmaze.items():
        for ident in eps:
            assert used[(show, ident[0], ident[1])] == 1, \
                f"TVmaze episode {show} {ident} used != 1 time"

    # ---- 5. events --------------------------------------------------
    by_ident = {(ep["show"], ep["season"], ep["episodeNumber"]): ep
                for ep in episodes}
    for group, kind, members in EVENTS:
        positions = []
        for idx, ident in enumerate(members, start=1):
            ep = by_ident[ident]
            ep["eventGroup"] = group
            ep["eventKind"] = kind
            ep["eventIndex"] = idx
            ep["eventTotal"] = len(members)
            positions.append(ep["order"])
        assert positions == sorted(positions), \
            f"event {group}: story order != watch order {positions}"
    notes["events"] = {g: len(m) for g, _k, m in EVENTS}

    envelope = {
        "universe": "arrowverse",
        "version": 1,
        "displayName": "Arrowverse",
        "showsLine": ("Arrow · The Flash · Supergirl · "
                      "DC's Legends of Tomorrow · Batwoman · "
                      "Black Lightning · Constantine · Superman & Lois"),
        "tvMazeShowIds": TVMAZE_IDS,
        "episodes": episodes,
    }
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(envelope, f, ensure_ascii=False, indent=1)
    with open(NOTES, "w", encoding="utf-8") as f:
        json.dump(notes, f, ensure_ascii=False, indent=1)
    print(f"wrote {OUT}: {len(episodes)} episodes, "
          f"{sum(1 for e in episodes if e['links'])} with links, "
          f"descriptions {len(episodes) - len(desc_missing)}/{len(episodes)}, "
          f"runtimes {len(episodes) - len(runtime_missing)}/{len(episodes)}")


def streaming_prime_gti(jw_ep):
    """GTI from a FLATRATE/ADS offer on a Prime package, else None.

    Purchase (BUY/RENT) offers are deliberately never consulted: their
    IDs open purchase pages, not streams."""
    for o in jw_ep["offers"]:
        if (o["gti"] and o["monetizationType"] in ("FLATRATE", "ADS")
                and "Prime" in (o["package"] or "")):
            return o["gti"]
    return None


if __name__ == "__main__":
    main()
