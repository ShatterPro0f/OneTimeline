#!/usr/bin/env python3
"""Build episodes-mcu.json for the One Timeline MCU pack — STAGE 1
(order + TV metadata). fetch_mcu_ids.py then fills film metadata,
true runtimes and the Sony/Netflix links from JustWatch Canada,
and applies the banked Disney+/Netflix IDs.

Pipeline (mirrors build_csi_episodes.py, the CSI pipeline):
  - StorytellingDB's MCU timeline (storytellingdb.com, by Jason)
    is the source of truth for WATCH ORDER — episode-by-episode
    in-universe chronology (the project ruling, production-decisions.md
    §1F/§1H.4). The site renders its table from a Google Sheets
    CSV through the Ninja Tables WordPress plugin; the snapshot
    was retrieved through the table's own public data endpoint
    (the same request the page's CSV button makes) and is saved
    raw in mcu-work/raw/storytellingdb-table-raw.json.
  - TVmaze supplies season/episode identity, titles, air dates
    and descriptions for every TV episode (the order source's
    rows carry explicit S.E numbering, so the join is by
    identity, with titles as a sanity check).
  - Films, Special Presentations and shorts take their identity
    and release dates from the order source; their descriptions
    and runtimes come from JustWatch (fetch_mcu_ids.py).

Scope (the project rulings, production-decisions.md §1H.1 as corrected
2026-11-04 12:18 EST): LIVE ACTION ONLY. Animation is out (What
If…?, Eyes of Wakanda, Marvel Zombies, I Am Groot, and — already
absent from the order source — YFNSM and X-Men '97); Helstrom is
out on the canon finding; tie-in comics are out (not screen
content); Avengers: Doomsday is out (unreleased, 2026-12-18).
Everything live action is in, including the five Marvel
One-Shots, the three Team Thor shorts, the WHIH Newsfront web
videos and Slingshot.

Assembly notes (all verified in mcu-build-report.md):
  - The order source lists Slingshot as ONE lumped row; it is
    expanded here into its six TVmaze episodes at that slot.
  - The order source carries no Team Thor rows (its author
    excludes them); the three shorts are inserted at their story
    positions per the MCU Wiki: Parts 1–2 (Thor's Civil War
    absence) immediately after Captain America: Civil War, and
    Team Darryl (the Grandmaster as Darryl's new roommate)
    immediately after Thor: Ragnarok.
  - WHIH Newsfront: the order source's ten Web rows are the
    MCU Wiki's canonical ten segments (5 + 5); titles, numbering,
    dates and one-line synopses are the Wiki's (its 2015 segment
    1.03 is dated July 11 there, July 10 in the order source —
    the Wiki date is used).
  - The Stark Expo row stays a single lumped web entry, as the
    order source has it: the 2010 campaign was a promotional
    website with videos, not an enumerable video series
    (MCU Wiki, "Stark Expo/Promotional Campaign").

Network fetches fall back to the caches in mcu-work/raw/ so the
build is rerunnable offline.
"""
import html as html_mod
import json
import re
import sys
import urllib.request
from collections import Counter, defaultdict

WORK_DIR = "mcu-work"
EP_NAME_RE = re.compile(r"^(.*?)\s+(\d+)\.(\d+)\s*:\s*(.*)$")

# Order-source show name -> dataset show name (== TVmaze map key)
SHOW_MAP = {
    "Defenders": "The Defenders",
    "Punisher": "The Punisher",
    "Cloak and Dagger": "Cloak & Dagger",
    "She-Hulk": "She-Hulk: Attorney at Law",
    "Falcon and the Winter Soldier": "The Falcon and the Winter Soldier",
}
TVMAZE_IDS = {
    "Agents of S.H.I.E.L.D.": 31, "Agent Carter": 336, "Inhumans": 22928,
    "Daredevil": 1369, "Jessica Jones": 1370, "Luke Cage": 2174,
    "Iron Fist": 2175, "The Defenders": 2176, "The Punisher": 16544,
    "Runaways": 20172, "Cloak & Dagger": 15307,
    "Agents of S.H.I.E.L.D.: Slingshot": 23460,
    "WandaVision": 41748, "The Falcon and the Winter Soldier": 41749,
    "Loki": 41007, "Hawkeye": 43102, "Moon Knight": 43518,
    "Ms. Marvel": 43519, "She-Hulk: Attorney at Law": 43517,
    "Secret Invasion": 52352, "Echo": 58950, "Agatha All Along": 58948,
    "Ironheart": 47859, "Daredevil: Born Again": 63234,
    "Wonder Man": 62569, "VisionQuest": 64950,
}
OUT_SHOWS = {"Eyes of Wakanda", "What If...?", "Marvel Zombies", "Helstrom"}
SPECIALS = {"Werewolf by Night", "The Guardians of the Galaxy Holiday Special",
            "The Punisher: One Last Kill"}
UNRELEASED = {"Avengers: Doomsday"}

# WHIH Newsfront — the MCU Wiki's canonical segment list
# (raw/wiki-whih-s1.wikitext, raw/wiki-whih-s2.wikitext):
# (season, ep, title, airdate, wiki synopsis)
WHIH = [
    (1, 1, "WHIH Newsfront Promo", "2015-07-02",
     "Christine Everhart shares what's coming up on the next episode of NEWSFRONT."),
    (1, 2, "WHIH Newsfront Top Stories", "2015-07-07",
     "Christine Everhart reports on the latest news of the day."),
    (1, 3, "WHIH EXCLUSIVE: 2012 VistaCorp break-in security footage involving "
           "cyber-criminal Scott Lang", "2015-07-11",
     "WHIH just obtained the security footage that implicated Scott Lang in "
     "the crime against VistaCorp."),
    (1, 4, "WIRED Insider Interviews Darren Cross, CEO of Pym Technologies",
     "2015-07-13",
     "Darren Cross speaks with WIRED Insider about the future of Pym Technologies."),
    (1, 5, "WHIH EXCLUSIVE: Scott Lang Interview", "2015-07-16",
     "Christine Everhart talks one-on-one with cyber-criminal Scott Lang via "
     "video interview from San Quentin."),
    (2, 1, "AVENGERS IMPACT: A WHIH Newsfront Special Report", "2016-04-22",
     "The Avengers have saved our world... but at what cost? WHIH Newsfront "
     "returns with a special report. Join Christine Everhart and political "
     "correspondent Will Adams as they investigate \"The World the Hero "
     "Leaves Behind.\""),
    (2, 2, "WHIH Newsfront: The Cost of Saving the World", "2016-04-26",
     "Join Christine Everhart and political correspondent Will Adams as they "
     "ask viewers who should be responsible for all of the cost associated "
     "with the clean up and rebuilding process."),
    (2, 3, "WHIH Newsfront: The Avengers and The White House", "2016-04-28",
     "Join WHIH Newsfront as host Christine Everhart and political "
     "correspondent Will Adams debate the Avengers and the role that the "
     "White House and President Ellis should play."),
    (2, 4, "WHIH Newsfront Exclusive: President Ellis Discusses the Avengers",
     "2016-05-03",
     "WHIH Newsfront lands an exclusive interview with President Ellis. Join "
     "Christine Everhart and political correspondent Will Adams ask him about "
     "nominating Thaddeus Ross as Secretary of State plus his thoughts on "
     "regulating the Avengers."),
    (2, 5, "WHIH Breaking News: Attack in Lagos", "2016-05-03",
     "WHIH Newsfront goes live with breaking news of an attack in Lagos, "
     "Nigeria. Reports of Avengers on the scene are among other details "
     "explored by Christine Everhart, political correspondent Will Adams, "
     "and field reporter Jackson Norris."),
]
WHIH_SOURCE_ROWS_2015 = 5  # order-source rows in the 2015 cluster, in order
WHIH_SOURCE_ROWS_2016 = 5  # order-source rows in the 2016 cluster, in order

STARK_EXPO_DESCRIPTION = (
    "Promotional web campaign for the Stark Expo 2010: an in-universe "
    "website with videos, including a welcome message from Tony Stark "
    "and a nostalgic promotion for the 1974 Expo.")

# Team Thor — (episode, title, airdate, runtime, anchor film title).
# Synopses are parsed from the MCU Wiki pages at build time
# (raw/wiki-team-thor-part-N.wikitext, raw/wiki-team-darryl.wikitext).
TEAM_THOR = [
    (1, "Team Thor", "2016-08-28", 3, "wiki-part-1.wikitext",
     "Captain America: Civil War"),
    (2, "Team Thor: Part 2", "2017-02-28", 4, "wiki-part-2.wikitext",
     "Captain America: Civil War"),
    (3, "Team Darryl", "2018-02-20", 6, "wiki-team-darryl.wikitext",
     "Thor: Ragnarok"),
]


def fetch(url):
    req = urllib.request.Request(
        url, headers={"User-Agent": "Mozilla/5.0 (OneTimeline MCU pack data pipeline)"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode("utf-8")


def load_snapshot():
    """The StorytellingDB table rows, raw snapshot (see docstring)."""
    with open(f"{WORK_DIR}/raw/storytellingdb-table-raw.json", encoding="utf-8") as f:
        rows = json.load(f)
    rows.sort(key=lambda r: int(r["ninja_column_1"]))
    return rows


def strip_html(s):
    s = re.sub(r"<[^>]*>", " ", s or "")
    for a, b in [("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", '"'),
                 ("&#039;", "'"), ("&#39;", "'"), ("&rsquo;", "’"), ("&lsquo;", "‘"),
                 ("&rdquo;", "”"), ("&ldquo;", "“"), ("&hellip;", "…"), ("&nbsp;", " ")]:
        s = s.replace(a, b)
    return re.sub(r"\s+", " ", s).strip()


def fetch_tvmaze():
    out = {}
    for name, sid in TVMAZE_IDS.items():
        try:
            eps = json.loads(fetch(f"https://api.tvmaze.com/shows/{sid}/episodes"))
            with open(f"{WORK_DIR}/raw/tvmaze-{sid}.json", "w", encoding="utf-8") as f:
                json.dump(eps, f)
        except Exception as e:
            print(f"tvmaze fetch failed for {name} ({e}); using cache", file=sys.stderr)
            with open(f"{WORK_DIR}/raw/tvmaze-{sid}.json", encoding="utf-8") as f:
                eps = json.load(f)
        out[name] = {(e["season"], e["number"]): e
                     for e in eps if e.get("season") and e.get("number")}
    return out


def wiki_synopsis(fn):
    """Cleaned ==Synopsis== text from a saved MCU Wiki page."""
    with open(f"{WORK_DIR}/raw/{fn}", encoding="utf-8") as f:
        wt = f.read()
    m = re.search(r"== ?Synopsis ?==\s*\n(.+?)(\n==|\Z)", wt, re.S)
    text = m.group(1) if m else ""
    text = re.sub(r"\[\[(?:[^\]|]*\|)?([^\]]+)\]\]", r"\1", text)
    text = text.replace("''", "")
    return re.sub(r"\s+", " ", text).strip()


def base(order, key, show, season, ep, code, title, air_date, description):
    return {
        "order": order, "key": key, "show": show, "season": season,
        "episodeNumber": ep, "code": code, "title": title,
        "airDate": air_date, "description": description,
        "runtimeMinutes": None,  # fetch_mcu_ids.py fills from JustWatch/Wiki
        "cameos": [], "eventKind": None, "eventIndex": None,
        "eventTotal": None, "eventGroup": None,
        "links": [],  # fetch_mcu_ids.py fills links + banked IDs
    }


def main():
    rows = load_snapshot()
    tvmaze = fetch_tvmaze()
    entries = []       # (sort anchor, entry) — order assigned at the end
    notes = []
    title_warnings = []
    consumed_tv = defaultdict(set)   # show -> {(s, e)}
    whih_2015 = [w for w in WHIH if w[0] == 1]
    whih_2016 = [w for w in WHIH if w[0] == 2]
    whih_seen_2015 = whih_seen_2016 = 0
    counts = Counter()

    def emit(entry, source_row=None):
        if source_row is not None:
            entry["sheetNumber"] = source_row
        entries.append(entry)
        counts[entry.get("category") or
               ("film" if entry.get("kind") == "movie" else "episode")] += 1

    for r in rows:
        row_no = int(r["ninja_column_1"])
        media, name = r["media"], html_mod.unescape(r["name"]).strip()
        date = r["date"] or None
        if media in ("Graphic Novel", "Comic Issue"):
            counts["out:comic"] += 1
            continue
        if media == "TV Episode":
            m = EP_NAME_RE.match(name)
            assert m, f"unparseable episode row {row_no}: {name!r}"
            raw_show, s, e = m.group(1), int(m.group(2)), int(m.group(3))
            if raw_show in OUT_SHOWS:
                counts["out:animation-or-helstrom"] += 1
                continue
            show = SHOW_MAP.get(raw_show, raw_show)
            tv = tvmaze[show].get((s, e))
            assert tv is not None, f"row {row_no}: no TVmaze episode {show} {s}x{e}"
            consumed_tv[show].add((s, e))
            if tv["name"].strip() != m.group(4).strip():
                title_warnings.append(
                    f"row {row_no} {show} {s}x{e:02d}: source title "
                    f"{m.group(4).strip()!r} vs TVmaze {tv['name']!r} (TVmaze used)")
            ep = base(0, f"{show}|{s}|{e}", show, s, e, f"{s}x{e:02d}",
                      tv["name"], tv.get("airdate") or date,
                      strip_html(tv.get("summary")) or None)
            emit(ep, row_no)
            continue
        if media == "Film":
            if name in UNRELEASED:
                counts["out:unreleased"] += 1
                continue
            ep = base(0, f"{name}|1|1", name, 1, 1, (date or "")[:4], name, date, None)
            ep["kind"] = "movie"
            if name in SPECIALS:
                ep["category"] = "special"
            emit(ep, row_no)
            continue
        if media == "Short Film" and r["series"] == "One-Shot":
            show = "Agent Carter (One-Shot)" if name == "Agent Carter" else name
            ep = base(0, f"{show}|1|1", show, 1, 1, (date or "")[:4], name, date, None)
            ep["kind"] = "movie"
            ep["category"] = "one-shot"
            emit(ep, row_no)
            continue
        if media == "Short Film":
            # I Am Groot — animation, out of scope.
            assert "Groot" in name, f"unexpected Short Film row {row_no}: {name!r}"
            counts["out:animation-or-helstrom"] += 1
            continue
        if media == "Web":
            if name.startswith("Agents of S.H.I.E.L.D.: Slingshot"):
                show = "Agents of S.H.I.E.L.D.: Slingshot"
                for (s, e) in sorted(tvmaze[show]):
                    tv = tvmaze[show][(s, e)]
                    consumed_tv[show].add((s, e))
                    ep = base(0, f"{show}|{s}|{e}", show, s, e, f"{s}x{e:02d}",
                              tv["name"], tv.get("airdate") or date,
                              strip_html(tv.get("summary")) or None)
                    ep["category"] = "web"
                    emit(ep, row_no)
                notes.append(
                    f"row {row_no}: Slingshot lumped row expanded to its 6 "
                    f"TVmaze episodes at this slot")
                continue
            if "WHIH" in name.upper() or "WHiH" in name or "WIRED Insider" in name:
                if (date or "") < "2016-01-01":
                    w = whih_2015[whih_seen_2015]; whih_seen_2015 += 1
                else:
                    w = whih_2016[whih_seen_2016]; whih_seen_2016 += 1
                s, e, title, airdate, syn = w
                ep = base(0, f"WHIH Newsfront|{s}|{e}", "WHIH Newsfront", s, e,
                          f"{s}x{e:02d}", title, airdate, syn)
                ep["category"] = "web"
                emit(ep, row_no)
                continue
            if name == "Stark Expo Videos":
                ep = base(0, "Stark Expo Videos|1|1", "Stark Expo Videos", 1, 1,
                          (date or "")[:4], name, date, STARK_EXPO_DESCRIPTION)
                ep["category"] = "web"
                emit(ep, row_no)
                continue
            raise RuntimeError(f"unhandled Web row {row_no}: {name!r}")
        raise RuntimeError(f"unhandled row {row_no}: [{media}] {name!r}")

    assert whih_seen_2015 == WHIH_SOURCE_ROWS_2015 and whih_seen_2016 == WHIH_SOURCE_ROWS_2016

    # Every TVmaze episode of every mapped show consumed exactly once.
    for show, eps in tvmaze.items():
        missing = set(eps) - consumed_tv[show]
        assert not missing, f"{show}: TVmaze episodes not placed: {sorted(missing)[:5]}"

    # Team Thor insertions at their story positions (MCU Wiki).
    def insert_after(film_title, new_entries):
        idx = next(i for i, e in enumerate(entries) if e["title"] == film_title
                   and e.get("kind") == "movie" and e["show"] == film_title)
        for j, e in enumerate(new_entries):
            entries.insert(idx + 1 + j, e)

    thor = []
    for num, title, airdate, runtime, fn, anchor in TEAM_THOR:
        ep = base(0, f"Team Thor|1|{num}", "Team Thor", 1, num, f"1x{num:02d}",
                  title, airdate, wiki_synopsis(fn))
        ep["kind"] = "movie"
        ep["category"] = "one-shot"
        ep["runtimeMinutes"] = runtime
        thor.append((anchor, ep))
    insert_after("Captain America: Civil War", [e for a, e in thor if a == "Captain America: Civil War"])
    insert_after("Thor: Ragnarok", [e for a, e in thor if a == "Thor: Ragnarok"])
    counts["one-shot"] += 3
    notes.append("Team Thor Parts 1–2 inserted after Captain America: Civil War; "
                 "Team Darryl after Thor: Ragnarok (MCU Wiki placements)")

    for i, e in enumerate(entries):
        e["order"] = i + 1
        # stable field order: order first, sheetNumber second (CSI shape)
        ordered = {"order": e.pop("order")}
        if "sheetNumber" in e:
            ordered["sheetNumber"] = e.pop("sheetNumber")
        ordered.update(e)
        entries[i] = ordered

    out = {"universe": "mcu", "version": 1, "episodes": entries}
    out_path = sys.argv[1] if len(sys.argv) > 1 else f"{WORK_DIR}/episodes-mcu.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)

    per_show = Counter(e["show"] for e in entries)
    n_desc = sum(1 for e in entries if e["description"])
    print(f"total={len(entries)} categories={dict(counts)}")
    print(f"descriptions={n_desc}/{len(entries)}")
    print(f"per-show={dict(sorted(per_show.items(), key=lambda kv: -kv[1]))}")
    for n in notes:
        print(f"NOTE: {n}")
    for w in title_warnings:
        print(f"TITLE NOTE: {w}")
    with open(f"{WORK_DIR}/build-notes.json", "w", encoding="utf-8") as f:
        json.dump({"counts": dict(counts), "perShow": dict(per_show),
                   "notes": notes, "titleWarnings": title_warnings}, f, indent=1)


if __name__ == "__main__":
    main()
