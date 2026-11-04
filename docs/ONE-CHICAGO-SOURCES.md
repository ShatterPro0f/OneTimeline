# One Chicago — sources and how they are used

This document accounts for **every kind of data** in the One Chicago universe pack: where it was found, how it was obtained, its licence or terms position, and what the app does with it. It is the worked example of the rule in [CREATING-A-UNIVERSE.md](CREATING-A-UNIVERSE.md): a universe ships in the release package only with a document like this one.

**The pack in numbers:** 846 episodes · 827 with Prime Video links · 845 with descriptions · 846 with true runtimes · 129 episodes flagged as part of a crossover or multi-part event · 686 cameo entries.

The data was assembled by two build scripts, both included in this repository under `tools/` so the pack can be rebuilt and audited: `build_episodes.py` (spreadsheet + TVmaze) and `fetch_prime_ids.py` (Prime Video matching).

---

## 1. Watch order, episode identity, and titles — the community spreadsheet

- **Source:** "Chicago Franchise Episode Timeline (with Cameos)", a public Google Sheet maintained by its author and community contributors: <https://docs.google.com/spreadsheets/d/1d6nnW_I3qrWUujOXi1Db2717wUKX86J4wRZdGOYPDog>
- **What was taken:** the episodes' sequence across all the shows (the watch order itself), and for each episode its show, season, episode number, title, and air date. The sheet lists **849 rows**; **3 are the same episode appearing on two tabs** (a series tab and a crossover tab). Duplicates were removed by episode identity (`Show|Season|Episode`), first occurrence in sheet order winning — leaving the **846** episodes in the pack.
- **How it was obtained:** the sheet's public export, read by `build_episodes.py`. No account or permission was involved; the sheet is published for viewers.
- **Licence/terms position:** no licence is stated on the sheet. The ordering and episode facts are used as factual data, the spreadsheet is **credited by name and link** in the app (Settings → About), in this repository's README, and at the top of the episode-links file. The sheet's own written episode *notes* (its author's prose) are **not** shipped in the app — see §4.
- **How the app uses it:** the watch order is the tracker's sequence and the Up Next list; show/season/episode/title are the episode identities shown everywhere.

## 2. Episode descriptions and fallback air dates — TVmaze

- **Source:** the [TVmaze](https://www.tvmaze.com) API (`api.tvmaze.com`), show IDs: Chicago Fire `59`, Chicago P.D. `56`, Chicago Med `2246`, Chicago Justice `12327`, FBI `32158`, Law & Order: SVU `103`.
- **What was taken:** the per-episode description text, and an air date only where the spreadsheet's was missing. **845 of 846** episodes have a description; the one without is an episode the spreadsheet lists ahead of broadcast (a future *Chicago Fire* episode with no title or description anywhere yet).
- **How it was obtained:** TVmaze's free public API, matched by show + season + episode number, in `build_episodes.py`. At runtime, the app's refresh also talks to TVmaze directly — and *only* TVmaze — to fill in descriptions and air dates for episodes it already knows.
- **Licence:** TVmaze data is **CC BY-SA**. The app credits TVmaze under every description it renders and in Settings → About, as the licence requires.
- **Deliberately NOT taken:** TVmaze's per-episode `runtime` is the broadcast *slot* length (typically 60 minutes), not the episode's length. Using it would break the app's end-of-episode detection, so runtimes come from §3 instead.

## 3. Prime Video episode links and true runtimes — JustWatch (Canada)

- **Source:** [JustWatch](https://www.justwatch.com)'s public web API (the same unauthenticated GraphQL endpoint its website uses), queried for **Canada**, matched per episode by show, season, and episode number via `fetch_prime_ids.py`.
- **What was taken:**
  - **Prime Video content IDs** (`amzn1.dv.gti.…`) — **827 of 846** episodes have one. The 19 without are all 13 *Chicago Justice* episodes (not listed for Prime Video Canada at match time) and the 6 most recent episodes (watch-order positions 841–846, not yet posted). Every returned ID was verified unique — no two episodes share one.
  - **True content runtimes** for all 846 episodes (median 42 minutes) — the episode's actual length, which the app's end detection is judged against.
- **Licence/terms position, stated plainly:** JustWatch's published terms describe personal, non-commercial use. This project is free and non-commercial, the IDs are Amazon's own content identifiers (they name an episode page; they are not video content), and no JustWatch prose, artwork, or pricing data is used. The maintainer's decision to include the matched IDs, with the sources named here, is recorded in the project's decision log.
- **How the app uses them:** a content ID is what the app hands to the TV to open that exact episode in Prime Video (and to resume it). A runtime is used only for end-of-episode detection during background watching. Neither is displayed as data in the app.

## 4. Crossover / multi-part flags and cameo entries — derived from the spreadsheet

- **Source:** the spreadsheet in §1 — specifically its episode notes (which mark crossovers, two-part stories, and visiting characters) and title arcs (e.g. episodes sharing an arc title).
- **What was derived, and how:** `build_episodes.py` applies a fixed set of derivation rules to the notes and titles to produce, per episode: an **event group** tying together the episodes of one crossover or multi-part story, each episode's position and the group's size within it (**129 episodes** are in an event), and **cameo entries** — short factual notes of a visiting character and their home show (**686 entries**).
- **What was NOT taken:** the notes' wording itself. The spreadsheet author's prose stays in the spreadsheet; the app ships only the factual skeleton derived from it (which episodes group together, who appears).
- **How the app uses it:** event groups drive the crossover banners, event strips, and badges in the tracker; cameo entries are shown on the episode's page.

## 5. What the app itself fetches after install

So there are no surprises later: once installed, the app talks to **TVmaze only** (descriptions and air-date gaps for episodes already in the pack — the refresh **never** changes the watch order, adds episodes, or touches links or runtimes), plus your Roku TV on your own network. Everything in §§1–4 was gathered at build time and ships inside the universe pack.

## Credits summary

- Watch order and episode facts: the **"Chicago Franchise Episode Timeline (with Cameos)"** spreadsheet and its contributors.
- Descriptions: **TVmaze**, CC BY-SA.
- Prime Video links and runtimes: matched via **JustWatch** (Canada); the IDs themselves are Amazon's.
- One Timeline is a fan project, not affiliated with NBC, NBCUniversal, Wolf Entertainment, Roku, Amazon, or The CW.
