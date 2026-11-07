#!/usr/bin/env python3
"""Build episodes-csi.json for the One Timeline CSI pack.

Pipeline (mirrors build_episodes.py, the Chicago pipeline):
  - Google Sheet "CSI Universe Release Order" (project-supplied) is the
    source of truth for WATCH ORDER and episode titles/dates as
    broadcast. It carries no usable season/episode numbering (its
    Code/Number columns are production numbering — see
    csi-research.md §1b), so:
  - TVmaze supplies season/episode numbers (via a title+airdate
    join), descriptions, and canonical titles/air dates.
  - fetch_csi_ids.py then fills primeContentId + runtimeMinutes
    from JustWatch Canada.

Scope (the project ruling, production-decisions.md §1E): the five CSI
series + Without a Trace S6E6 "Where and Why" as a single cameo
episode. Cold Case and the rest of Without a Trace are excluded.

The sheet counts each two-hour finale ("Grave Danger", "Immortality")
as ONE row; every catalogue counts two episodes, so those rows are
split into two pack episodes each: 837 in-scope sheet rows become
839 pack episodes.

Events/cameos are NOT in the sheet (it has no notes); the seven
crossover events and the one cameo annotation are encoded below
from the franchise record, as verified in csi-research.md §4.

Network fetches fall back to the research caches in csi-work/ when
a fetch fails, so the build is rerunnable offline.
"""
import csv
import io
import json
import re
import sys
import urllib.request
from datetime import datetime

SHEET_ID = "1I-LQamAPqCOM9PvG4IfCadLpwRGKr9hZ56BNWkBE0_I"
WORK_DIR = "csi-work"

# Sheet show name -> (pack show code, TVmaze show id)
SHOWS = {
    "CSI: Crime Scene Investigation": ("CSI", 109),
    "CSI: Miami": ("Miami", 576),
    "CSI: NY": ("NY", 749),
    "CSI: Cyber": ("Cyber", 735),
    "CSI: Vegas": ("Vegas", 50361),
}
WAT_SHOW = "Without a Trace"
WAT_CODE, WAT_TVMAZE_ID = "WAT", 813
WAT_SHEET_ORDER = 600  # "Where and Why" — the one in-scope WAT row

TVMAZE_IDS = {code: sid for code, sid in
              [(v[0], v[1]) for v in SHOWS.values()]} | {WAT_CODE: WAT_TVMAZE_ID}

# Documented sheet-title defects (csi-research.md §1b): the sheet's
# spelling on the left, TVmaze's on the right, both in normalised
# space (see norm_title). Applied to sheet titles before joining.
TITLE_ALIASES = {
    "the i 15 murders": "i 15 murders",       # sheet adds a spurious "The"
    "felonious monk": "felonius monk",        # sheet/TVmaze spelling split
    "sweet sixteen": "sweet 16",              # NY S3E9
    "rush to judgement": "rush to judgment",  # NY S5E11 (spelling)
    "redemptio": "redemption",                # NY S6E18 (sheet truncated)
}

# The seven verified crossover events (csi-research.md §4), members
# in story order. Singles are the backdoor pilots (a new show's lead
# introduced inside a parent-show episode).
EVENTS = [
    ("cross-jurisdictions", ["CSI|2|22"]),
    ("mia-nyc-nonstop", ["Miami|2|23"]),
    ("felony-flight-manhattan-manhunt", ["Miami|4|7", "NY|2|7"]),
    ("who-and-where", ["CSI|8|6", "WAT|6|6"]),
    ("csi-trilogy", ["Miami|8|7", "NY|6|7", "CSI|10|7"]),
    ("in-vino-veritas-seth-and-apep", ["CSI|13|13", "NY|9|15"]),
    ("kitty", ["CSI|14|21"]),
]

# Cameo annotations (factual appearances by characters from shows
# outside the pack; csi-research.md §4). "Two and a Half Deaths" is
# deliberately absent: a writers'-swap stunt cameo, not a story
# connection.
CAMEOS = {
    "NY|3|22": [{"show": "Cold Case", "names": "Scotty Valens"}],
}


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode("utf-8")


def fetch_sheet_rows():
    try:
        text = fetch(f"https://docs.google.com/spreadsheets/d/{SHEET_ID}/export?format=csv")
        with open(f"{WORK_DIR}/sheet.csv", "w", encoding="utf-8") as f:
            f.write(text)
    except Exception as e:
        print(f"sheet fetch failed ({e}); using csi-work/sheet.csv", file=sys.stderr)
        with open(f"{WORK_DIR}/sheet.csv", encoding="utf-8") as f:
            text = f.read()
    rows = []
    for r in csv.reader(io.StringIO(text)):
        if not r or not r[0].isdigit():
            continue
        rows.append({
            "sheetOrder": int(r[0]),
            "show": r[3].strip(),
            "title": r[4].strip().strip('"').strip(),
            "airDate": datetime.strptime(r[5].strip(), "%B %d, %Y").strftime("%Y-%m-%d"),
        })
    return rows


def strip_html(html):
    s = re.sub(r"<[^>]*>", " ", html or "")
    for a, b in [("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", '"'),
                 ("&#039;", "'"), ("&#39;", "'"), ("&rsquo;", "’"), ("&lsquo;", "‘"),
                 ("&rdquo;", "”"), ("&ldquo;", "“"), ("&hellip;", "…"), ("&nbsp;", " ")]:
        s = s.replace(a, b)
    return re.sub(r"\s+", " ", s).strip()


def fetch_tvmaze():
    out = {}  # show code -> list of episode dicts
    for code, sid in TVMAZE_IDS.items():
        try:
            eps = json.loads(fetch(f"https://api.tvmaze.com/shows/{sid}/episodes"))
            with open(f"{WORK_DIR}/tvmaze-{sid}.json", "w", encoding="utf-8") as f:
                json.dump(eps, f)
        except Exception as e:
            print(f"tvmaze fetch failed for {code} ({e}); using cache", file=sys.stderr)
            with open(f"{WORK_DIR}/tvmaze-{sid}.json", encoding="utf-8") as f:
                eps = json.load(f)
        out[code] = [e for e in eps if e.get("season") and e.get("number")]
        print(f"tvmaze {code}: {len(out[code])} episodes", file=sys.stderr)
    return out


def norm_title(t):
    t = (t or "").strip().strip('"').strip()
    t = t.replace("‘", "'").replace("’", "'").replace("…", "")
    t = t.replace("&", " and ").replace("-", " ").replace("—", " ")
    t = re.sub(r"[''’.:,!?]", "", t)
    t = t.replace("°", "")
    t = re.sub(r"(\d)\s+([a-z])\b", r"\1\2", t, flags=re.I)  # "58' W" vs TVmaze "58'W"
    t = re.sub(r"\s*\((?:part\s*)?[ivx\d]+\)\s*$", "", t, flags=re.I)
    t = re.sub(r"\s*\(featuring[^)]*\)\s*$", "", t, flags=re.I)
    t = re.sub(r"[:,]?\s*part\s+(?:\d+|[ivx]+)\s*$", "", t, flags=re.I)
    return re.sub(r"\s+", " ", t).strip().lower()


def main():
    sheet_rows = fetch_sheet_rows()
    in_scope = []
    for r in sheet_rows:
        if r["show"] in SHOWS:
            r["code"] = SHOWS[r["show"]][0]
            in_scope.append(r)
        elif r["show"] == WAT_SHOW and r["sheetOrder"] == WAT_SHEET_ORDER:
            r["code"] = WAT_CODE
            in_scope.append(r)
    print(f"sheet rows: {len(sheet_rows)}, in scope: {len(in_scope)}", file=sys.stderr)

    tvmaze = fetch_tvmaze()

    # Index TVmaze episodes per show by (normalised title, airdate).
    index = {}
    for code, eps in tvmaze.items():
        for e in eps:
            key = (code, norm_title(e["name"]), e.get("airdate"))
            index.setdefault(key, []).append(e)

    matched_tv = set()  # (code, season, number) consumed
    episodes = []
    notes = []
    unmatched = []
    order = 0

    def find(code, nt, air_date):
        """Join passes, strictest first. Returns (hits, how)."""
        hits = index.get((code, nt, air_date), [])
        if hits:
            return hits, "title+airdate"
        hits = [e for (c, t, d), es in index.items()
                if c == code and t == nt for e in es]
        if len(hits) == 1:
            return hits, "title-only"
        if len(hits) > 1:
            raise RuntimeError(f"AMBIGUOUS title-only join: {code} {nt!r}")
        squashed = nt.replace(" ", "")
        hits = [e for (c, t, d), es in index.items()
                if c == code and t.replace(" ", "") == squashed and d == air_date
                for e in es]
        if len(hits) == 1:
            return hits, "squashed-title+airdate"
        if len(hits) > 1:
            raise RuntimeError(f"AMBIGUOUS squashed join: {code} {nt!r}")
        hits = [e for (c, t, d), es in index.items()
                if c == code and t.replace(" ", "") == squashed for e in es]
        if len(hits) == 1:
            return hits, "squashed-title-only"
        if len(hits) > 1:
            raise RuntimeError(f"AMBIGUOUS squashed title-only join: {code} {nt!r}")
        return [], ""

    for r in in_scope:
        code = r["code"]
        nt = norm_title(r["title"])
        nt = TITLE_ALIASES.get(nt, nt)
        hits, how = find(code, nt, r["airDate"])
        if not hits:
            unmatched.append(r)
            continue
        hits = sorted(hits, key=lambda e: e["number"])
        if len(hits) > 1:
            how += f" + SPLITx{len(hits)}"
        if how != "title+airdate":
            notes.append(f"sheet {r['sheetOrder']} {code} {r['title']!r}: joined via {how}")
        for e in hits:
            ident = (code, e["season"], e["number"])
            if ident in matched_tv:
                raise RuntimeError(f"TVmaze episode matched twice: {ident}")
            matched_tv.add(ident)
            order += 1
            episodes.append({
                "order": order,
                "key": f"{code}|{e['season']}|{e['number']}",
                "sheetNumber": r["sheetOrder"],
                "show": code,
                "season": e["season"],
                "episodeNumber": e["number"],
                "code": f"{e['season']}x{e['number']:02d}",
                "title": e["name"],
                "airDate": e.get("airdate") or r["airDate"],
                "description": strip_html(e.get("summary")) or None,
                "runtimeMinutes": None,  # fetch_csi_ids.py fills from JustWatch
                "cameos": CAMEOS.get(f"{code}|{e['season']}|{e['number']}", []),
                "eventKind": None, "eventIndex": None, "eventTotal": None, "eventGroup": None,
                "primeContentId": "",   # fetch_csi_ids.py fills from JustWatch
            })

    if unmatched:
        for r in unmatched:
            print(f"UNMATCHED sheet {r['sheetOrder']}: {r['show']} {r['title']!r} {r['airDate']}")
        raise RuntimeError(f"{len(unmatched)} sheet row(s) did not join to TVmaze")

    # Every TVmaze episode of the five CSI shows must be consumed
    # exactly once; of Without a Trace, exactly S6E6.
    for code, eps in tvmaze.items():
        for e in eps:
            ident = (code, e["season"], e["number"])
            if code == WAT_CODE:
                if ident == (WAT_CODE, 6, 6):
                    assert ident in matched_tv, "WAT cameo not matched"
                else:
                    assert ident not in matched_tv, f"WAT {ident} leaked into the pack"
            else:
                assert ident in matched_tv, f"TVmaze episode never matched: {ident} {e['name']!r}"

    # Events.
    by_key = {ep["key"]: ep for ep in episodes}
    for group, keys in EVENTS:
        for i, k in enumerate(keys):
            ep = by_key[k]
            ep["eventKind"] = "crossover"
            ep["eventIndex"] = i + 1
            ep["eventTotal"] = len(keys)
            ep["eventGroup"] = group

    out = {"universe": "csi", "version": 1, "episodes": episodes}
    out_path = sys.argv[1] if len(sys.argv) > 1 else "episodes-csi.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)

    from collections import Counter
    counts = Counter(ep["show"] for ep in episodes)
    n_desc = sum(1 for ep in episodes if ep["description"])
    n_ev = sum(1 for ep in episodes if ep["eventKind"])
    print(f"total={len(episodes)} per-show={dict(counts)}")
    print(f"descriptions={n_desc} eventFlagged={n_ev}")
    for n in notes:
        print(f"JOIN NOTE: {n}")
    assert len(episodes) == 839, f"expected 839 episodes, got {len(episodes)}"
    assert dict(counts) == {"CSI": 337, "Miami": 232, "NY": 197,
                            "Cyber": 31, "Vegas": 41, "WAT": 1}, dict(counts)


if __name__ == "__main__":
    main()
