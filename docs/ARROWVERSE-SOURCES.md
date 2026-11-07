# Arrowverse — sources and how they are used

This document accounts for **every kind of data** in the Arrowverse pack: where it was found, how it was obtained, its licence or terms position, and what the app does with it. It follows the standard set by [ONE-CHICAGO-SOURCES.md](ONE-CHICAGO-SOURCES.md), [CSI-SOURCES.md](CSI-SOURCES.md) and [MCU-SOURCES.md](MCU-SOURCES.md), per the rule in [CREATING-A-UNIVERSE.md](CREATING-A-UNIVERSE.md): no sources document, no place in the release.

**The pack in numbers:** 765 episodes across 8 series — *Arrow* 170 · *The Flash* 184 · *Supergirl* 126 · *DC's Legends of Tomorrow* 110 · *Batwoman* 51 · *Black Lightning* 58 · *Constantine* 13 · *Superman & Lois* 53 — with 765 descriptions, 765 true runtimes, 8 crossover/multi-part event groups (28 episodes), 2 cameo-flagged episodes, and 353 streaming links — *Arrow*'s 169 on Prime Video Canada plus *The Flash*'s 184 on Netflix Canada (the stage-B harvest, filled in as pack dataVersion 2 on 2026-11-04). The remaining 412 episodes are checklist-only in Canada; §5 explains why, show by show.

**Form of the pack (Decision 1J):** this universe ships as a **downloadable pack**, not as data embedded in the app. The dataset file (`episodes-arrowverse.json`) is self-describing: its envelope carries `displayName`, `showsLine`, and the `tvMazeShowIds` refresh mapping alongside `universe` and `version`, so a downloaded pack registers from its envelope alone.

The data was assembled by two build scripts, included in this repository under `tools/` so the pack can be rebuilt and audited: `build_arrowverse_episodes.py` (order snapshot + TVmaze + assembly) and `fetch_arrowverse_ids.py` (JustWatch Canada offers, runtimes, and availability). A full research report on this pack's sources was written before the build (maintainer's records, 2026-11-03); the findings that matter are repeated here, and every availability figure below was **re-verified against JustWatch Canada on 2026-11-04**, the day of the build.

---

## 1. Watch order — arrowverse.info

- **Source:** [arrowverse.info](https://arrowverse.info/) — "Arrowverse Episode Order", a single flat episode-level list (position, series, episode code, title, original air date) maintained since 2014 by Jethro (GitHub: AceFire6); the site's code and data pipeline are open source ([AceFire6/ordered-arrowverse](https://github.com/AceFire6/ordered-arrowverse)), and its episode data is drawn from the Arrowverse Wiki with episodes ordered by air date.
- **How it was obtained:** the full list was retrieved over HTTPS on **2026-11-04** and parsed programmatically (830 rows); the raw page snapshot is retained in the maintainer's records.
- **What was taken:** the **sequence itself** for the in-scope series — the pack's watch order is the list's order, entry for entry, with each row's position in the full list kept in the pack as `sheetNumber`.
- **The scope arithmetic (the project rulings, decision log §1H.6):** of the 830 rows, **41 are *Stargirl*** (out — an Earth-2 series whose only connection is a Crisis cameo), **12 are *Vixen*** and **12 are *Freedom Fighters: The Ray*** (out — the animated web series). 830 − 41 − 12 − 12 = **765 episodes**, and the per-show counts equal TVmaze's final counts exactly for all eight series (every one of these series has ended, so the counts are final).
- **One keying reconciliation — Legends season 5.** The list counts the *Crisis on Infinite Earths* finale as a Legends special, "S05E0", and numbers the season's regular run S5E1–E14 from there. TVmaze keys the Crisis episode as a regular **S5E1** and the run as S5E2–E15. The pack keys the whole block **TVmaze's way** (order-source S5E0 → pack `5|1`, S5E1 → `5|2`, …, S5E14 → `5|15`): the app's episode identity requires an episode number of at least 1, and its metadata refresh joins on the TVmaze key. The sequence is unchanged — only the numbering follows the description source. The build asserts that all 15 shifted rows' titles agree between the two sources, and they do.
- **Licence/terms position:** the ordering and episode facts are used as factual data; arrowverse.info is credited here and in the app (Settings → About).
- **How the app uses it:** the order is the tracker's sequence and the Up Next list; every other data kind hangs off it.

## 2. Episode identity, descriptions, and air dates — TVmaze

- **Source:** the [TVmaze](https://www.tvmaze.com) API (`api.tvmaze.com`): *Arrow* `4`, *The Flash* `13`, *Supergirl* `1850`, *DC's Legends of Tomorrow* `1851`, *Batwoman* `37776`, *Black Lightning* `20683`, *Constantine* `15`, *Superman & Lois* `44751`. (The same mapping travels inside the pack's envelope as `tvMazeShowIds`, so a downloaded pack can refresh itself. Note for maintainers: TVmaze id `528` is the **1990** *Flash* series, not this one.)
- **What was taken:** for all **765 episodes**: season and episode numbers (the pack's identity keys), canonical title, description, and air date. Recap/clip specials listed by TVmaze are excluded; only regular episodes are pack content. The build verifies the join in both directions: the order list's episode set and TVmaze's regular-episode set are identical for every series.
- **Coverage:** descriptions **765 of 765** — TVmaze carries a summary for every episode in this pack.
- **Titles:** where the order source and TVmaze differ, TVmaze's title is used. All 54 such cases are capitalisation- or punctuation-level ("The Flash is Born" vs "The Flash Is Born", "Al Sah-him" vs "Al Sah-Him") and each is logged by the build script.
- **Licence:** TVmaze data is **CC BY-SA**. The app credits TVmaze under every description it renders and in Settings → About, as the licence requires.
- **Deliberately NOT taken:** TVmaze's per-episode runtimes — for these series they are broadcast slot lengths (60 minutes for 764 episodes, 90 for one), not content lengths. Runtimes come from §3 instead.

## 3. True runtimes — JustWatch (Canada)

- **Source:** [JustWatch](https://www.justwatch.com)'s public web API (the same unauthenticated GraphQL endpoint its website uses), queried for **Canada** by `fetch_arrowverse_ids.py`, via the show → season → episode chain.
- **What was taken:** the **true streamed runtime for all 765 episodes** (765 of 765 coverage) — the length of the episode itself, which the app judges episode endings against. Each runtime was matched to its pack entry by show, season, and episode number within JustWatch's own episode listing for that series (episode counts on JustWatch matched TVmaze exactly for all eight shows, and episode titles agree apart from JustWatch's part-marker suffixes and styling variants, which the build logs).

## 4. Streaming links, stage A — Prime Video Canada (Arrow only)

- **The rule (decision log §1H.6 as amended 2026-11-04):** a streaming link is written only from a listing that actually **streams** in Canada. JustWatch classifies every offer by monetization type; the pack takes a Prime Video content ID (`amzn1.dv.gti.…`) only from an offer classified **FLATRATE** (or its ad-supported tier) on a Prime package. **Purchase and rental listings are never used as a source of IDs** — their pages open a storefront, not a stream.
- **What that yields today:** *Arrow* is the only in-scope series that streams on Prime Video in Canada — **169 of its 170 episodes** carry a `prime` link in the pack, every ID taken from the episode's flatrate listing and verified unique and formally valid.
- **The one gap, accounted:** *Arrow* **S4E8, "Legends of Yesterday"** (part 2 of the *Heroes Join Forces* crossover), has **no flatrate listing in Canada** on JustWatch — it is purchase-only (Amazon Video and Apple TV Store) — so it ships without a link, as a checklist entry, rather than with a purchase-page ID.
- **Deliberately excluded — *The Flash*'s Prime IDs.** JustWatch carries a Prime content ID for every *Flash* episode, but all of them hang off **purchase** listings; the series streams in Canada on **Netflix** (§5). Under the rule above, none of those IDs is in the pack. The *Flash* Netflix episode IDs were collected by a separate signed-in harvest (stage B, under the maintainer's blanket platform permission, decision log §1H.7) and arrived as a pack data update — see the stage-C note below.
- **Licence/terms position, stated plainly:** JustWatch's published terms describe personal, non-commercial use. This project is free and non-commercial; the IDs are the platform's own content identifiers (they name a title page; they are not video content), and no JustWatch prose, artwork, or pricing data is used. The maintainer's decision to include the matched data, with the sources named here, is recorded in the project's decision log.

### Stage C update (2026-11-04) — *The Flash* on Netflix Canada

- **What arrived:** all **184** *Flash* episodes now carry a `netflix` link (the bare numeric watch ID from the episode's own Netflix watch address), filled into the pack as **dataVersion 2** by a links-append-only pass (`fill_arrowverse_flash_ids.py`, a maintainer workspace pipeline, on the same pattern as the MCU fill): no episode content other than `links` changed, verified by canonical comparison with links stripped.
- **How the IDs were obtained (stage B):** a signed-in browser harvest on Netflix Canada under §1H.7's blanket permission — metadata only, low volume, nothing played (the browser's protected-content error appears before playback). At the maintainer's direction every one of the 184 IDs was **individually click-verified** — each episode's own play control was clicked and the ID read from the address it landed on; **zero mismatches** across the whole pass, so no link in the pack rests on inference from neighbouring episode numbers (several seasons' first episodes sit outside their season's main ID block, which is exactly what the check was for).
- **Still excluded:** *The Flash*'s Prime IDs (purchase listings only), exactly as before.

## 5. Canada availability — the full picture (re-verified 2026-11-04)

Per JustWatch Canada, checked episode by episode for all eight series on the day of the build:

| Series | Episodes | Streams in Canada on | Purchase-only on | In the pack |
|---|---|---|---|---|
| Arrow | 170 | **Prime Video** (169 of 170) | Amazon Video, Apple TV Store | 169 Prime links; S4E8 checklist-only |
| The Flash | 184 | **Netflix** (all 184) | Amazon Video, Apple TV Store | 184 Netflix links (stage-B harvest, filled at dataVersion 2) |
| Supergirl | 126 | — nothing — | Apple TV Store | Checklist-only |
| DC's Legends of Tomorrow | 110 | — nothing — | Apple TV Store | Checklist-only |
| Batwoman | 51 | — nothing — | Apple TV Store | Checklist-only |
| Black Lightning | 58 | — nothing — | Apple TV Store | Checklist-only |
| Constantine | 13 | — nothing — | Apple TV Store | Checklist-only |
| Superman & Lois | 53 | — nothing — | Amazon Video (S1–S3 only), Apple TV Store | Checklist-only |

So **596 of the pack's 765 episodes have no streaming link**: 184 because their service (Netflix) IDs are a separate harvest, 411 because the series do not stream anywhere in Canada at all, and 1 (the *Arrow* episode above) because it is purchase-only despite its series streaming. Availability moves over time; a series that starts streaming in Canada later can gain links in a pack data update without any app change.

## 6. Crossover events and cameos — Arrowverse Wiki (corroboration)

- **Source:** the event articles of the [Arrowverse Wiki](https://arrow.fandom.com/wiki/Arrowverse_Wiki) (Fandom, CC BY-SA), as mapped in the maintainer's pre-build research; membership was cross-checked against the episodes' positions in the watch order (§1).
- **The standing rule (CREATING-A-UNIVERSE.md):** an event group is one story told across **two or more** episodes. A single episode featuring another show's characters is a `cameos` note, never a one-part event.
- **The 8 event groups (28 episodes):** *Flash vs. Arrow* (2) · *Heroes Join Forces* (2) · *Invasion!* (4 — including *Supergirl*'s "Medusa", whose final scene is the event's lead-in) · *Duet* (2 — including "Star-Crossed", whose closing scene introduces it) · *Crisis on Earth-X* (4) · *Elseworlds* (3 — *Legends* sat this one out; its same-week episode is deliberately **not** flagged) · *Crisis on Infinite Earths* (6 — including the *Black Lightning* tie-in episode, which aired between Parts Two and Three) · *Armageddon* (5 — a *Flash* multi-part story; in watch order its parts are **not** contiguous, because other series aired between them, and the flags record story order, not adjacency).
- **The 2 cameo episodes:** *Supergirl* 1x18 "Worlds Finest" (Barry Allen / The Flash guest-stars — a complete story in one episode) and *Arrow* 4x05 "Haunted" (John Constantine's guest appearance — the tie that binds *Constantine*'s own season into this universe).
- **Deliberately not flagged:** the loose 2021 Diggle guest arc (*Batwoman* 2x16, *The Flash* 7x12, *Superman & Lois* 1x12, *Supergirl* 6x12) — the order source does not treat it as an event and the episodes share no story, so it stays unflagged rather than being promoted into one.

## 7. Framing notes

- ***Superman & Lois*** is included in full (the project ruling): the order source sequences it with the rest of the franchise, and it crossed over via the Diggle appearance above — even though its second-season finale revealed it sits on a different Earth from post-Crisis Earth-Prime. It is Arrowverse by brand, cast, and the order document, and the pack treats it that way.
- ***Constantine*** is included in full: its single season is bound into the universe by Matt Ryan's continuing role (beginning with the "Haunted" cameo in §6), and it is part of the order document's list.
- **No link in this pack points at a purchase page, and no episode's data was invented:** where a source had nothing (one episode's streaming listing, five series' streaming rights), the pack says so by omission and this document says so in words.
