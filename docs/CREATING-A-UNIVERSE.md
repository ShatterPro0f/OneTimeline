# Creating your own universe

A **universe** in One Timeline is a pack of interwoven series presented as **one chronological watch order**: every episode of every show in the universe, sequenced the way the story actually unfolds, crossovers included. One Chicago is the first universe. This file is the complete instruction for authoring another one.

Right now, universes are authored as **data files** (below) and included in the app through a release. An in-app universe creator is planned; the file format documented here is the same format that creator will produce, so a universe authored by hand today stays valid.

## The golden rule: document your sources

**A universe is only added to the release package if it comes with a sources document.** No exceptions, including for the maintainer — the One Chicago pack's own document, [ONE-CHICAGO-SOURCES.md](ONE-CHICAGO-SOURCES.md), is the standard to match.

For **each kind of data** in your universe (watch order, titles, descriptions, episode links, runtimes, crossover flags), the sources document must state:

- **Where it came from** — the source's name and a link to it
- **How you got it** — by hand, an API, a published spreadsheet, a script (include the script or the exact steps)
- **Its licence or terms**, as best you can determine, and what that means for including the data
- **How the app uses it** — what the user sees or what feature it powers

If a kind of data has no lawful or documentable source, leave that data out (a universe with no episode links still tracks perfectly well — episodes without links simply have no Play button).

## The universe file

A universe is one JSON file with this envelope:

```json
{
  "universe": "arrowverse",
  "version": 1,
  "credit": "Optional but encouraged: who compiled this universe and from what headline sources.",
  "episodes": [ ... ]
}
```

- `universe` — a short lowercase ID, unique per universe (`one-chicago` is taken). Letters, digits, and hyphens.
- `version` — currently `1`.
- `episodes` — the episodes **in watch order**. The array order is presentational only; the `order` field (below) is what the app sorts by.

### Episode fields

| Field | Required | Meaning |
|---|---|---|
| `key` | **Yes** | Unique episode identity: `Show\|Season\|Episode`, e.g. `Fire\|1\|1`. The show part must match the episode's `show` value. No duplicates — an episode appears once, at its watch-order position. |
| `show` | **Yes** | The series name as you want it displayed, e.g. `Chicago Fire`, `The Flash`. |
| `season` | **Yes** | Season number within its show (integer ≥ 1). |
| `episodeNumber` | **Yes** | Episode number within its season (integer ≥ 1). Together with `show`, this must match `key`. |
| `order` | **Yes** | **The watch-order position across the whole universe**: 1, 2, 3, … with no gaps and no duplicates. This field *is* the universe. |
| `code` | **Yes** | Display code within the show, e.g. `1x01`, `3x08`. |
| `title` | **Yes** | The episode's title. Use a placeholder like `TBA` only for genuinely unannounced episodes. |
| `airDate` | No | Original air date, `YYYY-MM-DD`. |
| `runtimeMinutes` | No | The episode's **true content runtime** in minutes (see the warning below). |
| `description` | No | A short episode synopsis. Only include descriptions you have the right to use, and credit their source in your sources document (the Chicago pack's are TVmaze's, CC BY-SA, and are credited in the app). |
| `kind` | No | `"movie"` for a standalone entry — a film, a Special Presentation, a One-Shot — and absent (or `"episode"`) for a series episode. The kind decides the deep-link media type the app sends (`movie` vs `episode`); streaming services route the two differently. |
| `category` | No | The entry's content category, for the user's per-universe category switches: one of `film`, `episode`, `special`, `one-shot`, `web`. Only set it when the derivation (below) would be wrong — most entries need no `category` field at all. See **Content categories**. |
| `links` | No | The entry's playable streaming IDs, as `[{ "platform": "…", "id": "…" }, …]` — see **Streaming links**. Entries with a usable link get a working Play button; entries without one simply don't. Array order is your priority order. |
| `primeContentId` | No | **Legacy form** of a Prime Video link: the episode's Prime Video content ID (`amzn1.dv.gti.…`). Still fully supported — at load it is treated exactly as a `links` entry with platform `prime` — but new packs should use `links`, which covers all three services. |
| `eventGroup` | No | An ID shared by every episode in one crossover or multi-part event, e.g. `crossover-2016-winter`. Only for a story told across **2 or more episodes** (see the event rule below). Leave null/absent for standalone episodes. |
| `eventKind` | No | `crossover` or `multi-part` — set on every episode in the group. |
| `eventIndex` | No | This episode's position **within its event group** (1-based). |
| `eventTotal` | No | How many episodes the event group has in total. **Must be ≥ 2** — a group of one is not an event (see the event rule below). |
| `cameos` | No | Notable guest appearances as short strings, e.g. `["Trask (SVU)"]`. Factual appearance data only. |

**Runtime warning.** `runtimeMinutes` must be the length of the episode itself (≈42 minutes for an hour-long drama), **not** the broadcast slot length (60). The app judges episode endings against this number; slot lengths silently break end detection. If you don't have true runtimes, omit the field rather than guessing.

### Streaming links

An entry carries its playable IDs in `links`: a list of `{ "platform": …, "id": … }` objects, **at most one per platform**. The `platform` is a signifier — exactly one of:

| Signifier | Service | The `id` is |
|---|---|---|
| `prime` | Prime Video | The content ID (GTI): `amzn1.dv.gti.…` |
| `netflix` | Netflix | The episode's **watch ID**: the number in its `netflix.com/watch/<id>` address |
| `disney` | Disney+ | The entry's **entity/play UUID**: the UUID in its `disneyplus.com` address (after `/browse/entity-` or `/play/`), sent **without** any `entity-` prefix |

Per-platform notes that matter:

- **Netflix — episode IDs only.** A *series* ID (the one on a show's own page) does **not** open an episode; it merely resumes the show. Every episode needs its own watch ID.
- **Disney+ — UUIDs only.** Older ID forms (series slugs, the IDs in some wiki links) do not deep-link; the app rejects them at load. The UUID from the entry's own Disney+ address — the one the Disney+ app produces when you share the title, or the `/play/` address in a browser — is the form that works.
- **Prime — channel add-ons ride along.** A GTI for a channel add-on title (e.g. the CSI pack's Paramount+ channel IDs) is a normal `prime` link; it plays inside Prime Video for users who have the add-on.
- **Multiple links, one priority rule.** An entry may carry links on several services. Array order is the author's priority: when the user presses Play, the app plays the **first** link whose service the user has (their *My Services* selection in the app) **and** whose channel is installed on their TV. Author the order for the universe's home audience — the service most of them will have comes first.
- **Users can override.** A user can replace any single link for an entry with their own ID on the entry's page; theirs wins over the pack's for that platform. A user whose services don't cover an entry at all sees it as a plain checklist entry — no Play button — and ticks it off by hand. Leaving links out entirely is always safe.

### Content categories

Every entry has a **category** — what kind of content it is — used by the app's per-universe category switches. The category is **derived** unless the `category` field says otherwise:

- `kind: "movie"` with no `category` → **`film`**
- anything else with no `category` → **`episode`**
- an explicit `category` always wins over the derivation.

The known values are **`film`**, **`episode`**, **`special`**, **`one-shot`**, and **`web`**. Typical uses: a theatrical film is `kind: "movie"` and needs no category (it derives `film`); a Special Presentation is `kind: "movie"` + `category: "special"`; a One-Shot short is `kind: "movie"` + `category: "one-shot"`; a web series' episodes are ordinary episode entries + `category: "web"`. No other values are allowed — the app's validation rejects a pack that invents one, so if a universe genuinely needs a new category, it needs an app update first.

In the app, a universe whose entries span **more than one** category offers its users a switch per category (in Settings, and in setup). Switching a category off removes its entries from that user's tracker and Up Next without touching their progress; switching it back on restores them. Universes with a single category (all-episode packs like One Chicago and CSI) show no switches. Authoring guidance: don't use categories to split hairs — one category per *kind of thing a viewer might reasonably want to skip as a class* (the films, the one-shots), never per show or per season.

**Event rule.** Event groups are only for **one story told across two or more episodes**. A single episode that features another show's characters — including a **backdoor pilot** that introduces a future series, whose story opens and closes inside its own episode — is a `cameos` entry on that episode, **never** a one-part event. `eventTotal` 1 renders in the app as "Part 1 of 1", which is nonsense. (This rule is written down because the CSI pack's first version shipped three backdoor pilots as one-episode events; an audit converted them to cameos, and the pack's validation now rejects any one-episode group.)

### Ordering rules

- `order` expresses **story chronology across all shows together**, not per-show airing order — that interleaving is the entire point of a universe.
- Crossover and multi-part episodes sit at their story positions and are tied together with the `event*` fields, so the app can flag the whole event.
- Cameo appearances from shows outside the universe's main series belong **in** the order at their story position, marked with their own `show` name (the Chicago pack includes *Law & Order: SVU* and *FBI* episodes this way).
- One identity, one row: if a source lists an episode twice (in a series tab and a crossover tab, say), it appears **once**, at its first watch-order position.
- **Never lump.** Every entry is one individually playable item at its own story position — episodes one by one, and films, specials and one-shots as their own rows where they fall in the chronology. Never collapse a season into a single row because nothing else seems to interleave with it; the next crossover found later would have nowhere to go.

## The sources document

Ship it beside the universe file, named `<universe>-SOURCES.md` (the Chicago one is `ONE-CHICAGO-SOURCES.md`). One section per data kind, following the golden rule above: source name + link, how the data was obtained, its licence/terms position, and how the app uses it. Include any build scripts you used, or link to them — the next maintainer should be able to rebuild your universe from your document alone.

## Submitting a universe

1. Open an issue or pull request in this repository with:
   - the universe JSON file, and
   - its sources document.
2. The maintainer checks: the file validates against this spec (unique keys, gapless `order`, matching `key`/`show`/`season`/`episodeNumber`), the counts are sane, the sources document is complete, and credits are present.
3. If it passes, the universe is registered in the app's catalogue and ships in a release, where it appears in the setup and Settings universe lists like any other.

Universes that arrive without a complete sources document are not merged — that's the rule the Chicago pack itself is held to.
