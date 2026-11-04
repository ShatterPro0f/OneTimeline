#!/usr/bin/env python3
"""Stage-1 metadata/link pass for the One Timeline MCU pack.

Sibling to build_mcu_episodes.py (run it first) and modelled on
fetch_csi_ids.py. Fills, in mcu-work/episodes-mcu.json:

  - Films / Special Presentations / One-Shots: synopsis + runtime
    from JustWatch Canada (search-discovered Movie nodes). One-Shot
    descriptions fall back to MCU Wiki plot summaries when
    JustWatch has no record (the shorts are disc extras).
  - Sony Spider-Man films (Homecoming, Far From Home, No Way
    Home): FLATRATE Amazon-Channel offers carrying a Prime GTI
    (Crave/Starz channel listings on JustWatch CA) are written as
    links [{platform: "prime", id: GTI}] and flagged as channel
    links in the fetch report. Purchase offers never yield an ID.
  - TV episodes: true streamed runtimes from JustWatch episode
    records, applied only when the JustWatch title matches the
    dataset title (normalised). Fallbacks, in order: the TVmaze
    episode runtime already in the dataset, then the TVmaze
    show-level average runtime (counted separately as "average").
  - Banked IDs: Loki's 12 Disney+ episode IDs
    (mcu-work/disney-ids-loki.json) and Far From Home's Netflix
    title ID 81055822 (mcu-work/netflix-ids.json).

The final write carries the SELF-DESCRIBING pack envelope
(Decision 1J: downloadable pack, no catalogue registration):
{universe, version, displayName, showsLine, tvMazeShowIds,
episodes}. Also emits mcu-work/disney-harvest-list.json (every
entry still needing a Disney+ ID, grouped by title, Loki and the
non-Disney+ titles excluded) and mcu-work/pack-index-entry.json
(draft index entry with the dataset's sha256).

Re-runnable; --cache reuses the JustWatch/TVmaze-show caches in
mcu-work/raw/ instead of fetching.
"""
import hashlib
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from collections import Counter, OrderedDict

JW_URL = "https://apis.justwatch.com/graphql"
WORK_DIR = "mcu-work"
RAW = f"{WORK_DIR}/raw"

DISPLAY_NAME = "Marvel Cinematic Universe"
SHOWS_LINE = ("Every film and live-action series — the Avengers, the Defenders, "
              "Agents of S.H.I.E.L.D. and the Disney+ originals — plus Special "
              "Presentations, One-Shots and web shorts")

TVMAZE_SHOW_IDS = OrderedDict([
    ("Agents of S.H.I.E.L.D.", 31),
    ("Agent Carter", 336),
    ("Inhumans", 22928),
    ("Daredevil", 1369),
    ("Jessica Jones", 1370),
    ("Luke Cage", 2174),
    ("Iron Fist", 2175),
    ("The Defenders", 2176),
    ("The Punisher", 16544),
    ("Runaways", 20172),
    ("Cloak & Dagger", 15307),
    ("Agents of S.H.I.E.L.D.: Slingshot", 23460),
    ("WandaVision", 41748),
    ("The Falcon and the Winter Soldier", 41749),
    ("Loki", 41007),
    ("Hawkeye", 43102),
    ("Moon Knight", 43518),
    ("Ms. Marvel", 43519),
    ("She-Hulk: Attorney at Law", 43517),
    ("Secret Invasion", 52352),
    ("Echo", 58950),
    ("Agatha All Along", 58948),
    ("Ironheart", 47859),
    ("Daredevil: Born Again", 63234),
    ("Wonder Man", 62569),
    ("VisionQuest", 64950),
])

SONY_FILMS = {"Spider-Man: Homecoming", "Spider-Man: Far From Home",
              "Spider-Man: No Way Home"}

# Shows whose entries never go on the Disney+ harvest list:
# Runaways is purchase-only in Canada (not on Disney+ CA);
# the Sony films are not Disney+ titles.
HARVEST_EXCLUDE_SHOWS = {"Runaways"} | SONY_FILMS
# Groups that DO go on the list but are flagged as not currently
# streamable on Disney+ CA per the JustWatch platform map, so
# stage 2 re-checks before hunting for IDs that may not exist.
HARVEST_NOTES = {
    "Agents of S.H.I.E.L.D.: Slingshot":
        "No JustWatch CA offer at research time; Disney+ CA availability to re-check in stage 2.",
    "Spider-Man: Brand New Day":
        "Theatrical/buy window at research time; not yet on Disney+ CA.",
    "WHIH Newsfront":
        "YouTube web series; not a Disney+ title — listed so stage 2 can confirm and close it out.",
    "Stark Expo Videos":
        "Promotional web campaign; not a Disney+ title — listed so stage 2 can confirm and close it out.",
}

WIKI_ONE_SHOT_PAGES = {
    "The Consultant": "Marvel One-Shot: The Consultant",
    "A Funny Thing Happened on the Way to Thor's Hammer":
        "Marvel One-Shot: A Funny Thing Happened on the Way to Thor's Hammer",
    "Item 47": "Marvel One-Shot: Item 47",
    "Agent Carter (One-Shot)": "Marvel One-Shot: Agent Carter",
    "All Hail the King": "Marvel One-Shot: All Hail the King",
}

GTI_RE = re.compile(r"(amzn1\.dv\.gti\.[0-9a-fA-F-]{36})")
NETFLIX_RE = re.compile(r"netflix\.com/(?:title|watch)/(\d{5,12})")
PART_SUFFIX_RE = re.compile(r"\s*\((?:I|II|III|IV|V|\d+)\)\s*$")

SEARCH_QUERY = """
query SearchTitles($query: String!) {
  searchTitles(country: "CA", language: "en", first: 10,
               filter: {searchQuery: $query}, source: "web") {
    edges {
      node {
        id
        objectType
        content(country: "CA", language: "en") { title originalReleaseYear }
      }
    }
  }
}
"""

MOVIE_QUERY_FULL = """
query GetMovie($id: ID!) {
  node(id: $id) {
    ... on Movie {
      content(country: "CA", language: "en") {
        title runtime shortDescription originalReleaseYear
      }
      offers(country: "CA", platform: WEB) {
        standardWebURL
        monetizationType
        package { clearName }
      }
    }
  }
}
"""

MOVIE_QUERY_SLIM = """
query GetMovie($id: ID!) {
  node(id: $id) {
    ... on Movie {
      content(country: "CA", language: "en") { title runtime originalReleaseYear }
      offers(country: "CA", platform: WEB) {
        standardWebURL
        monetizationType
        package { clearName }
      }
    }
  }
}
"""

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
        content(country: "CA", language: "en") { title episodeNumber runtime }
        offers(country: "CA", platform: WEB) {
          standardWebURL
          monetizationType
          package { clearName }
        }
      }
    }
  }
}
"""


def gql(query, variables):
    body = json.dumps({"query": query, "variables": variables}).encode()
    req = urllib.request.Request(
        JW_URL, data=body,
        headers={"Content-Type": "application/json",
                 "User-Agent": "Mozilla/5.0 (OneTimeline MCU pack data pipeline)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        out = json.loads(r.read().decode("utf-8"))
    if out.get("errors"):
        raise RuntimeError(f"GraphQL errors: {out['errors']}")
    return out["data"]


def norm_title(t):
    t = PART_SUFFIX_RE.sub("", (t or "").strip()).strip().lower()
    t = re.sub(r"[’']", "'", t)
    t = re.sub(r"[^a-z0-9' ]+", " ", t)
    return re.sub(r"\s+", " ", t).strip()


def titles_match(a, b):
    # '&' expands to 'and' BEFORE norm_title strips punctuation
    # ('Smoke & Mirrors' vs 'Smoke and Mirrors').
    na = norm_title((a or "").replace("&", " and "))
    nb = norm_title((b or "").replace("&", " and "))
    if not na or not nb:
        return False
    if na == nb:
        return True
    # Squashed comparison: punctuation/spacing-only variants of the
    # SAME full title — 'A.K.A X' vs 'AKA X'. Still a full-title
    # equality check, so a different episode can never pass it.
    if na.replace(" ", "") == nb.replace(" ", ""):
        return True
    # Part-numbering variants: accept when one side is a prefix of
    # the other at a word boundary.
    return na.startswith(nb + " ") or nb.startswith(na + " ")


# ----------------------------------------------------------------
# JustWatch discovery (cached)
# ----------------------------------------------------------------

def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_json(path, data):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)


_discovery = None


def norm_loose(t):
    """norm_title plus JustWatch branding equivalences: a stripped
    'Marvel's' / 'Marvel One-Shot:' prefix (JustWatch brands the
    Marvel Television shows 'Marvel's X' and the shorts 'Marvel
    One-Shot: X' while the dataset calls them 'X'), and digit/word
    equivalence ('The Fantastic 4' on JustWatch vs 'The Fantastic
    Four' in the dataset)."""
    t = norm_title(t)
    for prefix in ("marvel's ", "marvels ", "marvel one shot "):
        if t.startswith(prefix):
            t = t[len(prefix):]
            break
    words = {"2": "two", "3": "three", "4": "four"}
    return " ".join(words.get(w, w) for w in t.split(" "))


def _load_discovery():
    global _discovery
    if _discovery is None:
        try:
            _discovery = load_json(f"{RAW}/jw-discovery.json")
        except FileNotFoundError:
            _discovery = {}
    return _discovery


def _search_candidates(query, object_type):
    data = gql(SEARCH_QUERY, {"query": query})
    time.sleep(0.4)
    out = []
    for edge in (data.get("searchTitles") or {}).get("edges") or []:
        n = edge.get("node") or {}
        if n.get("objectType") != object_type:
            continue
        c = n.get("content") or {}
        out.append({"id": n["id"], "title": c.get("title") or "",
                    "year": c.get("originalReleaseYear")})
    return out


def discover_all(title, year, object_type):
    """Ranked JustWatch candidates for (title, year, object_type).

    Ranking: exact (loose) title match first, then closest release
    year. Title-exact candidates whose year is known to be far off
    (>2 years) are dropped — same-title different shows are a real
    failure mode here (The Defenders 1961 vs 2017, Runaways 2006
    vs 2017). Falls back to a 'Marvel's <title>' search for shows.
    Cached (v3 keys; earlier keys used weaker matching and are
    ignored).
    """
    cache = _load_discovery()
    key = f"v3all:{object_type}:{title}:{year}"
    if key in cache:
        return cache[key]
    ranked = []
    if "--cache" not in sys.argv:
        queries = [title]
        if object_type == "SHOW":
            queries.append(f"Marvel's {title}")
        seen, candidates = set(), []
        for q in queries:
            for c in _search_candidates(q, object_type):
                if c["id"] not in seen:
                    seen.add(c["id"])
                    candidates.append(c)
        exact = [c for c in candidates
                 if norm_loose(c["title"]) == norm_loose(title)]

        def year_gap(c):
            try:
                return abs(int(c["year"]) - int(year)) if year else 0
            except (TypeError, ValueError):
                return 99

        pool = []
        for c in exact:
            gap = year_gap(c)
            if c["year"] in (None, 0, "0") or gap <= 2:
                pool.append(c)
        pool.sort(key=year_gap)
        ranked = pool
    cache[key] = ranked
    save_json(f"{RAW}/jw-discovery.json", cache)
    return ranked


def discover(title, year, object_type):
    """Best JustWatch node for (title, year, object_type) or None."""
    ranked = discover_all(title, year, object_type)
    return ranked[0] if ranked else None


def fetch_movie(node_id):
    cache_path = f"{RAW}/jw-movie-{node_id}.json"
    if "--cache" in sys.argv:
        try:
            return load_json(cache_path)
        except FileNotFoundError:
            return None
    try:
        data = gql(MOVIE_QUERY_FULL, {"id": node_id})
    except RuntimeError:
        data = gql(MOVIE_QUERY_SLIM, {"id": node_id})
    time.sleep(0.4)
    node = data.get("node") or {}
    out = {"content": node.get("content") or {},
           "offers": node.get("offers") or []}
    save_json(cache_path, out)
    return out


def fetch_show_episodes(node_id):
    """-> {(season, ep): {title, runtime}}; cached like the CSI pass."""
    cache_path = f"{RAW}/jw-show-{node_id}.json"
    if "--cache" in sys.argv:
        try:
            raw = load_json(cache_path)
            return {(int(s), int(e)): v for (s, e), v in
                    ((k.split(":"), v) for k, v in raw.items())}
        except FileNotFoundError:
            return {}
    data = gql(SEASONS_QUERY, {"id": node_id})
    time.sleep(0.4)
    season_nodes = {}
    for s in (data["node"] or {}).get("seasons") or []:
        sn = (s.get("content") or {}).get("seasonNumber")
        if sn is not None:
            season_nodes[sn] = s["id"]
    out = {}
    for sn in sorted(season_nodes):
        data = gql(EPISODES_QUERY, {"id": season_nodes[sn]})
        time.sleep(0.4)
        for ep in (data["node"] or {}).get("episodes") or []:
            c = ep.get("content") or {}
            if c.get("episodeNumber") is None:
                continue
            rt = c.get("runtime")
            out[(sn, int(c["episodeNumber"]))] = {
                "title": c.get("title") or "",
                "runtime": int(rt) if isinstance(rt, (int, float)) and rt > 0 else None,
            }
    save_json(cache_path, {f"{s}:{e}": v for (s, e), v in out.items()})
    return out


def tvmaze_show_runtime(show_id):
    """TVmaze show-level average runtime (fallback), cached."""
    cache_path = f"{RAW}/tvmaze-show-{show_id}.json"
    if "--cache" in sys.argv:
        try:
            raw = load_json(cache_path)
        except FileNotFoundError:
            return None
    else:
        req = urllib.request.Request(
            f"https://api.tvmaze.com/shows/{show_id}",
            headers={"User-Agent": "OneTimeline MCU pack data pipeline"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                raw = json.loads(r.read().decode("utf-8"))
        except Exception:
            return None
        save_json(cache_path, raw)
        time.sleep(0.3)
    rt = raw.get("runtime")
    return int(rt) if isinstance(rt, (int, float)) and rt > 0 else None


def clean_wikitext(text):
    text = re.sub(r"\{\{[^{}]*\}\}", "", text)
    text = re.sub(r"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", r"\1", text)
    text = re.sub(r"\[https?://\S+\s+([^\]]*)\]", r"\1", text)
    text = re.sub(r"<[^>]+>", "", text)
    text = text.replace("'''", "").replace("''", "")
    return re.sub(r"\s+", " ", text).strip()


def wiki_plot(page_title):
    """First paragraph of the MCU Wiki page's Plot/Synopsis section."""
    q = urllib.parse.urlencode({
        "action": "parse", "page": page_title, "prop": "wikitext",
        "format": "json"})
    req = urllib.request.Request(
        f"https://marvelcinematicuniverse.fandom.com/api.php?{q}",
        headers={"User-Agent": "OneTimeline MCU pack data pipeline"})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            raw = json.loads(r.read().decode("utf-8"))
        wikitext = raw["parse"]["wikitext"]["*"]
    except Exception:
        return None
    m = re.search(r"==\s*(?:Plot|Synopsis)\s*==\s*\n(.*?)(?:\n==|\Z)",
                  wikitext, re.S)
    body = m.group(1) if m else wikitext
    for para in body.split("\n"):
        para = clean_wikitext(para)
        if len(para) >= 60:
            return para[:600]
    return None


def flatrate_offers(offers):
    return [o for o in offers or [] if o.get("monetizationType") == "FLATRATE"]


def offer_gti(offers):
    """(gti, package) from the first FLATRATE offer whose URL has one."""
    for o in flatrate_offers(offers):
        m = GTI_RE.search(o.get("standardWebURL") or "")
        if m:
            return m.group(1), ((o.get("package") or {}).get("clearName") or "")
    return None, None


def add_link(row, platform, link_id, report):
    for existing in row["links"]:
        if existing["platform"] == platform:
            if existing["id"] != link_id:
                report["warnings"].append(
                    f"link conflict {row['key']}: {platform} already "
                    f"{existing['id']}, new {link_id} (kept first)")
            return
    row["links"].append({"platform": platform, "id": link_id})


def main():
    data = load_json(f"{WORK_DIR}/episodes-mcu.json")
    rows = data["episodes"]
    report = {"warnings": [], "movieMisses": [], "showMisses": [],
              "runtimeSkippedMismatch": [], "channelLinks": [],
              "netflixCrossChecks": []}
    stats = Counter()

    # ---- 1. Films, specials, one-shots: JustWatch Movie nodes ----
    for row in rows:
        if row.get("kind") != "movie":
            continue
        title = row["title"]
        year = int(row["code"]) if str(row.get("code", "")).isdigit() else None
        node = discover(title, year, "MOVIE")
        if node is None:
            report["movieMisses"].append(
                {"key": row["key"], "title": title, "year": year})
            stats["movie:noNode"] += 1
            continue
        stats["movie:node"] += 1
        movie = fetch_movie(node["id"])
        if not movie:
            stats["movie:fetchFailed"] += 1
            continue
        content = movie["content"]
        desc = content.get("shortDescription")
        if desc and not row.get("description"):
            row["description"] = desc.strip()
            stats["movie:description"] += 1
        rt = content.get("runtime")
        if isinstance(rt, (int, float)) and rt > 0:
            row["runtimeMinutes"] = int(rt)
            stats["movie:runtime"] += 1
        gti, pkg = offer_gti(movie["offers"])
        if gti:
            add_link(row, "prime", gti, report)
            stats["movie:primeLink"] += 1
            report.setdefault("moviePrimeLinks", []).append(
                {"key": row["key"], "title": title, "package": pkg,
                 "gti": gti, "sony": title in SONY_FILMS})
            if title in SONY_FILMS:
                report["channelLinks"].append(
                    {"key": row["key"], "title": title, "package": pkg,
                     "gti": gti})
        for o in flatrate_offers(movie["offers"]):
            m = NETFLIX_RE.search(o.get("standardWebURL") or "")
            if m:
                report["netflixCrossChecks"].append(
                    {"key": row["key"], "title": title,
                     "netflixId": m.group(1)})

    # ---- 2. One-Shot description fallback: MCU Wiki plot ----
    if "--cache" not in sys.argv:
        for row in rows:
            if row.get("category") == "one-shot" and not row.get("description"):
                page = WIKI_ONE_SHOT_PAGES.get(row["show"])
                if page:
                    plot = wiki_plot(page)
                    time.sleep(0.3)
                    if plot:
                        row["description"] = plot
                        stats["oneShot:wikiDescription"] += 1

    # ---- 3. TV true runtimes from JustWatch episode records ----
    # Candidate nodes are tried in ranked order; a node is only
    # trusted when at least half of the show's dataset episodes
    # title-match its records (same-title wrong shows — The
    # Defenders 1961, Runaways 2006 — match ~0%).
    by_show = {}
    for row in rows:
        by_show.setdefault(row["show"], []).append(row)
    for show, show_rows in by_show.items():
        if show not in TVMAZE_SHOW_IDS:
            continue
        years = [int(r["airDate"][:4]) for r in show_rows
                 if (r.get("airDate") or "")[:4].isdigit()]
        year = min(years) if years else None
        candidates = discover_all(show, year, "SHOW")
        if not candidates:
            report["showMisses"].append(show)
            stats["tv:noNode"] += 1
            continue
        best = None  # (coverage, node, jw_eps, matched)
        for node in candidates[:3]:
            jw_eps = fetch_show_episodes(node["id"])
            if not jw_eps:
                continue
            matched = sum(
                1 for r in show_rows
                if (r["season"], r["episodeNumber"]) in jw_eps
                and titles_match(
                    jw_eps[(r["season"], r["episodeNumber"])]["title"],
                    r["title"]))
            coverage = matched / len(show_rows)
            if best is None or coverage > best[0]:
                best = (coverage, node, jw_eps, matched)
            if coverage >= 0.5:
                break
        if best is None:
            report["showMisses"].append(show)
            stats["tv:noNode"] += 1
            continue
        coverage, node, jw_eps, matched = best
        if coverage < 0.5:
            report.setdefault("showWrongNode", []).append(
                {"show": show, "node": node,
                 "coverage": round(coverage, 3)})
            stats["tv:wrongNode"] += 1
            if matched == 0:
                continue
        stats["tv:node"] += 1
        for row in show_rows:
            hit = jw_eps.get((row["season"], row["episodeNumber"]))
            if hit and hit["runtime"]:
                if titles_match(hit["title"], row["title"]):
                    if row.get("runtimeMinutes") != hit["runtime"]:
                        row["runtimeMinutes"] = hit["runtime"]
                    stats["tv:runtimeJW"] += 1
                else:
                    report["runtimeSkippedMismatch"].append(
                        {"key": row["key"], "dataset": row["title"],
                         "justwatch": hit["title"]})
                    stats["tv:runtimeSkipped"] += 1
            elif row.get("runtimeMinutes"):
                stats["tv:runtimeTVmazeKept"] += 1

    # ---- 4. TVmaze show-average runtime fallback ----
    for show, show_rows in by_show.items():
        if show not in TVMAZE_SHOW_IDS:
            continue
        if not any(not r.get("runtimeMinutes") for r in show_rows):
            continue
        avg = tvmaze_show_runtime(TVMAZE_SHOW_IDS[show])
        if avg:
            for row in show_rows:
                if not row.get("runtimeMinutes"):
                    row["runtimeMinutes"] = avg
                    stats["tv:runtimeAverage"] += 1

    # ---- 5. Banked IDs ----
    loki = load_json(f"{WORK_DIR}/disney-ids-loki.json")
    loki_ids = {(e["s"], e["e"]): e["disneyId"] for e in loki["episodes"]}
    for row in rows:
        if row["show"] == "Loki":
            did = loki_ids.get((row["season"], row["episodeNumber"]))
            if did:
                add_link(row, "disney", did, report)
                stats["links:lokiDisney"] += 1
    netflix = load_json(f"{WORK_DIR}/netflix-ids.json")
    for item in netflix["items"]:
        for row in rows:
            if row["show"] == item["title"]:
                add_link(row, "netflix", item["netflixId"], report)
                stats["links:bankedNetflix"] += 1

    # ---- 6. Self-describing envelope + final writes ----
    out = OrderedDict([
        ("universe", "mcu"),
        ("version", 1),
        ("displayName", DISPLAY_NAME),
        ("showsLine", SHOWS_LINE),
        ("tvMazeShowIds", dict(TVMAZE_SHOW_IDS)),
        ("episodes", rows),
    ])
    payload = json.dumps(out, ensure_ascii=False, indent=1)
    # Decision 1J: the MCU pack ships as a DOWNLOADABLE pack, not
    # baked into the app — the dataset lives in mcu-work/ only.
    # (The production csproj globs Resources\Raw\** as MauiAsset,
    # so a copy there would be embedded in the APK at build time.)
    with open(f"{WORK_DIR}/episodes-mcu.json", "w", encoding="utf-8") as f:
        f.write(payload)
    sha = hashlib.sha256(payload.encode("utf-8")).hexdigest()

    # ---- 7. Disney+ harvest list ----
    groups = OrderedDict()
    for row in rows:
        if row["show"] in HARVEST_EXCLUDE_SHOWS or row["show"] == "Loki":
            continue
        if any(l["platform"] == "disney" for l in row["links"]):
            continue
        g = groups.setdefault(row["show"], {
            "show": row["show"], "category": row.get("category") or
            ("film" if row.get("kind") == "movie" else "episode"),
            "entries": []})
        g["entries"].append({
            "key": row["key"], "season": row["season"],
            "episodeNumber": row["episodeNumber"], "title": row["title"]})
    harvest = []
    for show, g in groups.items():
        entry = {"show": show, "category": g["category"],
                 "episodeCount": len(g["entries"]), "entries": g["entries"]}
        if show in HARVEST_NOTES:
            entry["note"] = HARVEST_NOTES[show]
        harvest.append(entry)
    harvest_total = sum(g["episodeCount"] for g in harvest)
    save_json(f"{WORK_DIR}/disney-harvest-list.json", {
        "universe": "mcu",
        "generated": "2026-11-04",
        "totalEntries": harvest_total,
        "groups": harvest,
    })

    # ---- 8. Draft pack index entry ----
    save_json(f"{WORK_DIR}/pack-index-entry.json", OrderedDict([
        ("id", "mcu"),
        ("displayName", DISPLAY_NAME),
        ("showsLine", SHOWS_LINE),
        ("episodeCount", len(rows)),
        ("file", "episodes-mcu.json"),
        ("sha256", sha),
        ("dataVersion", 1),
    ]))

    report["stats"] = dict(stats)
    report["sha256"] = sha
    report["harvestTotal"] = harvest_total
    report["harvestGroups"] = len(harvest)
    save_json(f"{WORK_DIR}/fetch-report.json", report)

    linked = Counter()
    for row in rows:
        for l in row["links"]:
            linked[l["platform"]] += 1
    print(f"total={len(rows)} links={dict(linked)}")
    print(f"descriptions={sum(1 for r in rows if r.get('description'))}/{len(rows)}")
    print(f"runtimes={sum(1 for r in rows if r.get('runtimeMinutes'))}/{len(rows)}")
    print(f"stats={dict(stats)}")
    print(f"movieMisses={report['movieMisses']}")
    print(f"showMisses={report['showMisses']}")
    print(f"channelLinks={report['channelLinks']}")
    print(f"netflixCrossChecks={report['netflixCrossChecks']}")
    print(f"runtimeSkippedMismatch={len(report['runtimeSkippedMismatch'])}")
    for w in report["warnings"]:
        print(f"WARNING: {w}")
    print(f"harvest: {harvest_total} entries in {len(harvest)} groups")
    print(f"sha256={sha}")


if __name__ == "__main__":
    main()
