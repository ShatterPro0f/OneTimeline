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
| `primeContentId` | No | The episode's Prime Video content ID (`amzn1.dv.gti.…`). Episodes with one get a working Play button; episodes without one simply don't. |
| `eventGroup` | No | An ID shared by every episode in one crossover or multi-part event, e.g. `crossover-2016-winter`. Leave null/absent for standalone episodes. |
| `eventKind` | No | `crossover` or `multi-part` — set on every episode in the group. |
| `eventIndex` | No | This episode's position **within its event group** (1-based). |
| `eventTotal` | No | How many episodes the event group has in total. |
| `cameos` | No | Notable guest appearances as short strings, e.g. `["Trask (SVU)"]`. Factual appearance data only. |

**Runtime warning.** `runtimeMinutes` must be the length of the episode itself (≈42 minutes for an hour-long drama), **not** the broadcast slot length (60). The app judges episode endings against this number; slot lengths silently break end detection. If you don't have true runtimes, omit the field rather than guessing.

### Ordering rules

- `order` expresses **story chronology across all shows together**, not per-show airing order — that interleaving is the entire point of a universe.
- Crossover and multi-part episodes sit at their story positions and are tied together with the `event*` fields, so the app can flag the whole event.
- Cameo appearances from shows outside the universe's main series belong **in** the order at their story position, marked with their own `show` name (the Chicago pack includes *Law & Order: SVU* and *FBI* episodes this way).
- One identity, one row: if a source lists an episode twice (in a series tab and a crossover tab, say), it appears **once**, at its first watch-order position.

## The sources document

Ship it beside the universe file, named `<universe>-SOURCES.md` (the Chicago one is `ONE-CHICAGO-SOURCES.md`). One section per data kind, following the golden rule above: source name + link, how the data was obtained, its licence/terms position, and how the app uses it. Include any build scripts you used, or link to them — the next maintainer should be able to rebuild your universe from your document alone.

## Submitting a universe

1. Open an issue or pull request in this repository with:
   - the universe JSON file, and
   - its sources document.
2. The maintainer checks: the file validates against this spec (unique keys, gapless `order`, matching `key`/`show`/`season`/`episodeNumber`), the counts are sane, the sources document is complete, and credits are present.
3. If it passes, the universe is registered in the app's catalogue and ships in a release, where it appears in the setup and Settings universe lists like any other.

Universes that arrive without a complete sources document are not merged — that's the rule the Chicago pack itself is held to.
