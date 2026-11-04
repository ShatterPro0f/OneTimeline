#!/usr/bin/env python3
"""Build episodes.json for the One Chicago tracker (APK edition).

Replicates, rule for rule, the data pipeline of the verified web app
(ts-spaces/one-chicago-watch-through/server/src/actions.ts):
  - Google Sheet "Chicago Franchise Episode Timeline (with Cameos)" is the
    source of truth for order/show/code/title/air date/notes/cameos.
  - TVmaze supplies descriptions (+ fallback air dates) and, for the APK,
    runtime in minutes.
  - Cross-tab duplicates removed by identity (show|season|episode), first
    occurrence in sheet order wins.
  - Crossover / multi-part events derived from sheet notes + title arcs,
    exactly as the web app's deriveEvents().
"""
import json
import re
import sys
import urllib.request

SHEET_ID = "1d6nnW_I3qrWUujOXi1Db2717wUKX86J4wRZdGOYPDog"
TVMAZE_SHOW_IDS = {
    "Fire": 59,
    "PD": 56,
    "Med": 2246,
    "Justice": 12327,
    "FBI": 32158,
    "Law & Order: SVU": 103,
}
KNOWN_SHOWS = set(TVMAZE_SHOW_IDS)
CAMEO_LABELS = {
    "fire": "Fire",
    "pd": "PD",
    "med": "Med",
    "justice": "Justice",
    "law & order": "Law & Order",
    "fbi": "FBI",
}


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode("utf-8")


def cell_string(cell):
    if not cell or cell.get("v") is None:
        return None
    v = cell["v"]
    if isinstance(v, str):
        return v
    if isinstance(v, (int, float)):
        return str(int(v)) if float(v).is_integer() else str(v)
    return None


def cell_number(cell):
    if not cell or not isinstance(cell.get("v"), (int, float)):
        return None
    return cell["v"]


def parse_gviz_date(cell):
    raw = cell_string(cell)
    if not raw:
        return None
    m = re.match(r"^Date\((\d+),(\d+),(\d+)", raw)
    if m:
        y, mo, d = int(m.group(1)), int(m.group(2)) + 1, int(m.group(3))
        return f"{y:04d}-{mo:02d}-{d:02d}"
    m = re.match(r"^(\d{4})-(\d{2})-(\d{2})", raw)
    return f"{m.group(1)}-{m.group(2)}-{m.group(3)}" if m else None


def map_columns(cols):
    m = {"num": -1, "show": -1, "code": -1, "title": -1, "air": -1, "notes": -1, "cameos": []}
    for i, col in enumerate(cols):
        label = re.sub(r"\s+", " ", (col.get("label") or "")).strip().lower()
        if label.startswith("#"):
            m["num"] = i
        elif label == "show":
            m["show"] = i
        elif label == "episode":
            m["code"] = i
        elif "title" in label:
            m["title"] = i
        elif label.startswith("air date"):
            m["air"] = i
        elif label.startswith("notes"):
            m["notes"] = i
        elif label.startswith("cameos"):
            show = CAMEO_LABELS.get(re.sub(r"^cameos\s*", "", label).strip())
            if show:
                m["cameos"].append({"index": i, "show": show})
        elif m["notes"] >= 0 and label in CAMEO_LABELS:
            m["cameos"].append({"index": i, "show": CAMEO_LABELS[label]})
    if m["num"] < 0:
        m["num"] = 0
    if m["show"] < 0:
        m["show"] = 1
    if m["code"] < 0:
        m["code"] = 2
    if m["title"] < 0:
        m["title"] = 3
    return m


def parse_tab(table):
    m = map_columns(table.get("cols", []))
    out = []
    for row in table.get("rows", []):
        cells = row.get("c") or []
        def at(idx):
            return cells[idx] if 0 <= idx < len(cells) else None
        num = cell_number(at(m["num"]))
        if num is None or num <= 0:
            continue
        show = (cell_string(at(m["show"])) or "").strip()
        if show not in KNOWN_SHOWS:
            continue
        code = (cell_string(at(m["code"])) or "").strip()
        cm = re.match(r"^(\d+)x(\d+)$", code)
        if not cm:
            continue
        season, epnum = int(cm.group(1)), int(cm.group(2))
        title = (cell_string(at(m["title"])) or "").strip()
        if not title:
            continue
        air_date = parse_gviz_date(at(m["air"])) if m["air"] >= 0 else None
        note = None
        if m["notes"] >= 0:
            raw_note = (cell_string(at(m["notes"])) or "").strip()
            if raw_note and not raw_note.startswith("Date("):
                note = raw_note
        cameos = []
        for col in m["cameos"]:
            raw = (cell_string(at(col["index"])) or "").strip()
            if raw and raw not in ("–", "-"):
                cameos.append({"show": col["show"], "names": raw})
        out.append({
            "sheetNumber": int(num),
            "show": show,
            "season": season,
            "episodeNumber": epnum,
            "episodeCode": code,
            "title": title,
            "airDate": air_date,
            "sheetNote": note,
            "cameos": cameos,
        })
    return out


def fetch_sheet():
    html = fetch(f"https://docs.google.com/spreadsheets/d/{SHEET_ID}/htmlview")
    gids = []
    for match in re.finditer(r'items\.push\(\{name: "[^"]+",[\s\S]*?gid: "(\d+)"', html):
        gid = match.group(1)
        if gid not in gids:
            gids.append(gid)
    if not gids:
        raise RuntimeError("Could not find sheet tabs in the page")
    print(f"tabs: {len(gids)}", file=sys.stderr)
    all_eps, seen = [], set()
    raw_rows = 0
    for gid in gids:
        text = fetch(f"https://docs.google.com/spreadsheets/d/{SHEET_ID}/gviz/tq?tqx=out:json&gid={gid}")
        start, end = text.index("{"), text.rindex("}")
        parsed = json.loads(text[start:end + 1])
        table = parsed.get("table")
        if not table:
            continue
        for ep in parse_tab(table):
            raw_rows += 1
            identity = f"{ep['show']}|{ep['season']}|{ep['episodeNumber']}"
            if identity in seen:
                continue
            seen.add(identity)
            all_eps.append(ep)
    print(f"sheet rows parsed: {raw_rows}, unique: {len(all_eps)}", file=sys.stderr)
    if len(all_eps) < 100:
        raise RuntimeError(f"Sheet parse returned only {len(all_eps)} episodes")
    return all_eps


def strip_html(html):
    s = re.sub(r"<[^>]*>", " ", html)
    for a, b in [("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", '"'),
                 ("&#039;", "'"), ("&#39;", "'"), ("&rsquo;", "’"), ("&lsquo;", "‘"),
                 ("&rdquo;", "”"), ("&ldquo;", "“"), ("&hellip;", "…"), ("&nbsp;", " ")]:
        s = s.replace(a, b)
    return re.sub(r"\s+", " ", s).strip()


def fetch_tvmaze():
    out = {}
    for show, sid in TVMAZE_SHOW_IDS.items():
        show_json = json.loads(fetch(f"https://api.tvmaze.com/shows/{sid}"))
        avg_runtime = show_json.get("averageRuntime")
        eps = json.loads(fetch(f"https://api.tvmaze.com/shows/{sid}/episodes"))
        for ep in eps:
            if not ep.get("season") or not ep.get("number"):
                continue
            summary = strip_html(ep["summary"]) if ep.get("summary") else ""
            runtime = ep.get("runtime") or avg_runtime
            out[f"{show}|{ep['season']}|{ep['number']}"] = {
                "description": summary or None,
                "airDate": ep.get("airdate") or None,
                "runtimeMinutes": int(runtime) if runtime else None,
            }
        print(f"tvmaze {show}: {len(eps)} episodes", file=sys.stderr)
    return out


NOTE_RE = re.compile(r"(crossover|part)\s*(\d+)\s*/\s*(\d+)", re.I)
TITLE_RE = re.compile(r"^(.*?)[\s]*[:,]\s*Part\s+(I{1,3}|IV|V|\d+)\.?\s*$", re.I)


def derive_events(eps):
    result = [None] * len(eps)
    i = 0
    while i < len(eps):
        note = eps[i].get("sheetNote") or ""
        m = NOTE_RE.search(note)
        if not m:
            i += 1
            continue
        kind = "crossover" if m.group(1).lower() == "crossover" else "part"
        total = int(m.group(3))
        idx = int(m.group(2))
        group = f"note-{i}"
        j = i
        while j < len(eps):
            m2 = NOTE_RE.search(eps[j].get("sheetNote") or "")
            if not m2:
                break
            kind2 = "crossover" if m2.group(1).lower() == "crossover" else "part"
            if kind2 != kind or int(m2.group(3)) != total or int(m2.group(2)) != idx:
                break
            result[j] = {"kind": kind, "index": idx, "total": total, "group": group}
            idx += 1
            j += 1
        i = max(j, i + 1)
    i = 0
    while i < len(eps):
        if result[i]:
            i += 1
            continue
        tm = TITLE_RE.match(eps[i].get("title") or "")
        if not tm:
            i += 1
            continue
        base = tm.group(1).strip().lower()
        run = [i]
        j = i + 1
        while j < len(eps) and not result[j]:
            tm2 = TITLE_RE.match(eps[j].get("title") or "")
            if not tm2 or tm2.group(1).strip().lower() != base:
                break
            run.append(j)
            j += 1
        if len(run) >= 2:
            shows = {eps[k]["show"] for k in run}
            kind = "crossover" if len(shows) > 1 else "part"
            group = f"title-{i}"
            for n, k in enumerate(run):
                result[k] = {"kind": kind, "index": n + 1, "total": len(run), "group": group}
        i = max(j, i + 1)
    return result


def main():
    sheet_eps = fetch_sheet()
    tvmaze = fetch_tvmaze()
    events = derive_events(sheet_eps)
    rows = []
    for idx, ep in enumerate(sheet_eps):
        identity = f"{ep['show']}|{ep['season']}|{ep['episodeNumber']}"
        tv = tvmaze.get(identity, {})
        ev = events[idx]
        rows.append({
            "order": idx + 1,
            "key": identity,
            "sheetNumber": ep["sheetNumber"],
            "show": ep["show"],
            "season": ep["season"],
            "episodeNumber": ep["episodeNumber"],
            "code": ep["episodeCode"],
            "title": ep["title"],
            "airDate": ep["airDate"] or tv.get("airDate"),
            "description": tv.get("description"),
            "runtimeMinutes": tv.get("runtimeMinutes"),
            "note": ep["sheetNote"],
            "cameos": ep["cameos"],
            "eventKind": ev["kind"] if ev else None,
            "eventIndex": ev["index"] if ev else None,
            "eventTotal": ev["total"] if ev else None,
            "eventGroup": ev["group"] if ev else None,
            "primeContentId": "",
        })
    out_path = sys.argv[1] if len(sys.argv) > 1 else "episodes.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=1)
    n_desc = sum(1 for r in rows if r["description"])
    n_rt = sum(1 for r in rows if r["runtimeMinutes"])
    n_ev = sum(1 for r in rows if r["eventKind"])
    print(f"total={len(rows)} descriptions={n_desc} runtimes={n_rt} eventFlagged={n_ev}")
    e125 = rows[124]
    print(f"order125: {e125['show']} {e125['code']} {e125['title']!r} runtime={e125['runtimeMinutes']}")


if __name__ == "__main__":
    main()
