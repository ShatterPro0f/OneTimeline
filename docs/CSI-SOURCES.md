# CSI — sources and how they are used

This document accounts for **every kind of data** in the CSI universe pack: where it was found, how it was obtained, its licence or terms position, and what the app does with it. It follows the standard set by [ONE-CHICAGO-SOURCES.md](ONE-CHICAGO-SOURCES.md), per the rule in [CREATING-A-UNIVERSE.md](CREATING-A-UNIVERSE.md): a universe ships in the release package only with a document like this one.

**The pack in numbers:** 839 episodes · 803 with Prime Video links · 839 with descriptions · 839 with true runtimes · 9 episodes flagged as part of a crossover event · 14 episodes carrying cameo annotations.

The data was assembled by two build scripts, included in this repository under `tools/` so the pack can be rebuilt and audited: `build_csi_episodes.py` (spreadsheet + TVmaze) and `fetch_csi_ids.py` (Prime Video matching). A full research report on this pack's sources was written before the build (`csi-research.md` in the maintainer's records); the findings that matter are repeated here.

---

## 1. Watch order and episode identity — the community spreadsheet

- **Source:** "CSI Universe Release Order", a public Google Sheet: <https://docs.google.com/spreadsheets/d/1I-LQamAPqCOM9PvG4IfCadLpwRGKr9hZ56BNWkBE0_I>
- **Credit, stated plainly:** the sheet is **anonymous**. There is no author name, byline, or maintainer note anywhere in it, and its file metadata isn't readable through the public export. It is credited by title and link here and in the app (Settings → About), because that is all the credit the document itself makes possible.
- **What was taken:** the episodes' sequence across the five CSI series (the watch order itself — release order, verified to be perfectly chronological with every crossover's parts sitting consecutively), plus each episode's show, title, and release date for matching purposes. The sheet lists **1,152 rows**: the five CSI series (836 rows) plus all of *Without a Trace* (160) and *Cold Case* (156).
- **Scope applied to it:** this pack takes the five CSI series plus exactly one *Without a Trace* episode — **S6E6 "Where and Why"**, part 2 of the crossover begun by *CSI* S8E6 "Who & What" — the same cameo treatment the Chicago pack gives its guest-show episodes. The rest of *Without a Trace* and all of *Cold Case* are **excluded**: between them they contribute exactly one story-relevant episode (already included) and one character appearance inside an in-pack episode (handled as a cameo annotation, §4). Neither series' episodes continue any CSI story.
- **What was NOT taken:** the sheet's `Code` and `Number` columns. They look like season/episode numbering and are not — they are production numbering (within *CSI: Miami*'s first season alone they run 301→606, they repeat across episodes, and *CSI: Vegas*'s second season restarts mid-run at 701). Season and episode numbers come from TVmaze instead (§2). The sheet also has an unlabelled column of 8s, 9s, and 10s whose meaning could not be determined; it is dropped, and that is stated here rather than guessed at.
- **The finale reconciliation:** the sheet counts each of CSI's two **two-hour finales as one row**; every catalogue counts them as two episodes. "Grave Danger" (2005) and "Immortality" (2015) are therefore each split into their two catalogue episodes, which occupy consecutive watch-order positions. That is the whole arithmetic of the pack: 837 in-scope sheet rows → **839 episodes** (CSI 337, Miami 232, NY 197, Cyber 31, Vegas 41, Without a Trace 1).
- **Known sheet defects, corrected at build time:** one wrong date (*CSI: Miami* "Reality Kills", sheet says November 14, 2010; it aired November 7) and four defective titles (a spurious "The" in "The I-15 Murders", the truncated "Redemptio", and the spelling variants "Felonious Monk", "Sweet Sixteen", "Rush to Judgement"). Air dates and titles in the pack are the matched TVmaze values (§2); the watch order is always the sheet's.
- **How it was obtained:** the sheet's public CSV export, read by `build_csi_episodes.py`. No account or permission was involved; the sheet is published for viewers.
- **Licence/terms position:** no licence is stated on the sheet. The ordering and episode facts are used as factual data, and the sheet is credited by name and link as above.
- **How the app uses it:** the watch order is the tracker's sequence and the Up Next list; the order and identities are what every other data kind hangs off.

## 2. Season/episode numbers, descriptions, and air dates — TVmaze

- **Source:** the [TVmaze](https://www.tvmaze.com) API (`api.tvmaze.com`), show IDs: CSI: Crime Scene Investigation `109`, CSI: Miami `576`, CSI: NY `749`, CSI: Cyber `735`, CSI: Vegas `50361`, Without a Trace `813`.
- **What was taken:** for every episode, its **season and episode numbers** (the sheet has none — see §1), its **description**, its canonical **title**, and its **air date**. Coverage is **839 of 839** for descriptions.
- **How it was obtained:** TVmaze's free public API. Each spreadsheet row was matched to a TVmaze episode by (title, air date) within its show — 827 of the 836 CSI rows matched on the first pass; the remainder are the documented sheet defects in §1 plus title-format variants (part markers, punctuation, a "Featuring" credit in one NY title), each resolved deliberately and logged by the build script. The join is verified in both directions: every TVmaze episode of the five series is in the pack exactly once, and of *Without a Trace* exactly S6E6 is.
- **Licence:** TVmaze data is **CC BY-SA**. The app credits TVmaze under every description it renders and in Settings → About, as the licence requires.
- **Deliberately NOT taken:** TVmaze's per-episode `runtime` is the broadcast *slot* length (60 minutes), not the episode's length. Using it would break the app's end-of-episode detection, so runtimes come from §3 instead.

## 3. Prime Video episode links and true runtimes — JustWatch (Canada), via the Paramount+ channel

- **Source:** [JustWatch](https://www.justwatch.com)'s public web API (the same unauthenticated GraphQL endpoint its website uses), queried for **Canada**, matched per episode by show, season, and episode number via `fetch_csi_ids.py`.
- **The important difference from the Chicago pack:** *nothing* in this franchise streams on plain Prime Video in Canada. The links in this pack are **Paramount+ Amazon Channel** content IDs — the same `amzn1.dv.gti.…` identifiers as any Prime link, for episodes hosted inside Prime Video by the **Paramount+ add-on channel**. What that means for a viewer: the Play button opens the episode in Prime Video exactly as usual, but watching it requires a **Paramount+ channel subscription on top of Prime**. The app says this in its setup text and Settings → About, not just here.
- **What was taken:**
  - **Content IDs** — **803 of 839** episodes have one, every one of them verified unique. Per show: CSI 335 of 337, Miami 230 of 232, NY **197 of 197**, Vegas **41 of 41**, Cyber **0 of 31**, Without a Trace 0 of 1. The holes are: all of *CSI: Cyber* and the *Without a Trace* episode (both purchase-only in Canada at match time), two mid-run Miami episodes (S7E15 "Presumed Guilty", S7E16 "Sink or Swim", purchase-only), and the "Immortality" finale (JustWatch files it outside its season listings with no Canadian offers at all). Episodes without an ID simply have no Play button.
  - **True content runtimes** for 835 episodes (median ≈43 minutes) — the episode's actual length, which the app's end detection is judged against.
- **Two runtime corrections, disclosed:** JustWatch stamps the combined two-hour runtime (85 minutes) on *both* "Grave Danger" part records; since the parts are separate episodes with separate IDs, the combined figure is split across them (43 + 42). "Immortality" has no JustWatch record at all; its documented length as a television film is 88 minutes (Wikipedia), so each of its two catalogue episodes carries 44. With those, runtime coverage is **839 of 839**.
- **Licence/terms position, stated plainly:** JustWatch's published terms describe personal, non-commercial use. This project is free and non-commercial, the IDs are Amazon's own content identifiers (they name an episode page; they are not video content), and no JustWatch prose, artwork, or pricing data is used. The maintainer's decision to include the matched IDs, with the sources named here, is recorded in the project's decision log.
- **One honest caveat:** that a Paramount+ channel ID deep-links through the app's Prime-on-Roku path the same way a base-Prime ID does is **inferred from the identical ID format, not yet device-tested**. The IDs are genuine Prime Video content identifiers; the first on-device play from this pack is the test.
- **How the app uses them:** a content ID is what the app hands to the TV to open that exact episode in Prime Video (and to resume it). A runtime is used only for end-of-episode detection during background watching. Neither is displayed as data in the app.

## 4. Crossover events and cameo annotations — derived from the franchise record

- **Source:** the sheet in §1 has no notes, so event membership and story order come from the franchise record — principally the Paramount Wiki (Fandom) "CSI (franchise)" crossovers tables, which catalogue every inter-show connection by type, plus Wikipedia's event and episode articles ("CSI: Trilogy", "Kitty", "Two and a Half Deaths", the *CSI: Cyber* and *CSI: Vegas* season articles) and TVmaze guest-cast records, cross-checked against the sheet's own dates and positions. A full classification audit of every connection in the franchise was carried out on 2026-11-04; this section states its conclusions.
- **The classification rule:** an **event** is one story told across **two or more episodes**. A single episode that features another show's characters is a **cameo annotation**, never a one-part event.
- **The events — four, covering 9 episodes.** In every case the story order equals the broadcast order, and the sheet already sequences the parts consecutively:
  1. **Felony Flight → Manhattan Manhunt** — *CSI: Miami* S4E7 → *CSI: NY* S2E7.
  2. **Who & What → Where and Why** — *CSI* S8E6 → *Without a Trace* S6E6 (the event that brings the pack's one guest episode in).
  3. **CSI: Trilogy** — *CSI: Miami* S8E7 "Bone Voyage" → *CSI: NY* S6E7 "Hammer Down" → *CSI* S10E7 "The Lost Girls".
  4. **In Vino Veritas → Seth and Apep** — *CSI* S13E13 → *CSI: NY* S9E15.
- **The cameo annotations — fourteen episodes:**
  - *CSI* S2E22 **"Cross Jurisdictions"** — Horatio Caine and the Miami team (the *CSI: Miami* backdoor pilot).
  - *CSI: Miami* S2E23 **"MIA/NYC NonStop"** — Mac Taylor and the New York team (the *CSI: NY* backdoor pilot).
  - *CSI* S14E21 **"Kitty"** — Avery Ryan (the *CSI: Cyber* backdoor pilot).
  - *CSI* S15E6 **"The Twin Paradox"** — Avery Ryan consults again, profiling the Gig Harbor Killer.
  - *CSI: NY* S1E4 **"Grand Master"** — Disco Placid, the Las Vegas audio consultant first seen in *CSI*'s "Anonymous".
  - *CSI: NY* S3E22 **"Cold Reveal"** — **Scotty Valens (*Cold Case*)**: Danny Pino appears in character, inside a NY episode. That one-way appearance is *Cold Case*'s only on-screen connection to the family; no *Cold Case* episode is a crossover, which is why the series is excluded but the appearance is recorded — the same treatment the Chicago pack gives guest characters.
  - **Original-*CSI* guest returns in *CSI: Vegas* — eight episodes, nine appearances** (verified against TVmaze guest-cast records): **Jim Brass** in *Vegas* S1E1 "Legacy" and S1E2 "Honeymoon in Vegas"; **David Hodges** in S1E2 "Honeymoon in Vegas", S1E7 "In the Blood", S1E9 "Waiting in the Wings", and S1E10 "Signed, Sealed, Delivered"; **Greg Sanders** in S2E17 "The Promise", S2E18 "Fractured", and S2E19 "Dead Memories". ("Honeymoon in Vegas" hosts two of the nine appearances, so the eight episodes take the annotation count from six to fourteen.) The 2026-11-04 audit had classed these as sequel-cast continuity (see the exclusions below); **Sam ruled explicitly on 2026-11-04 that guest returns get cameo notes**, so they are recorded here. The sequel's returning *regulars* remain excluded as cast — see below.)
- **The backdoor-pilot correction, owned plainly:** the first version of this pack flagged the three backdoor pilots above ("Cross Jurisdictions", "MIA/NYC NonStop", "Kitty") as one-episode "crossover" events, which rendered in the app as the nonsense label **"Part 1 of 1"**. That was this project's error. A backdoor pilot is a *launch*, not a crossover story: its case opens and closes inside its own episode, and the spun-off show's premiere opens an unrelated case — the story never leaves the host episode. The 2026-11-04 audit reclassified all three as the cameo annotations they are, and the pack's build pipeline and dataset validation now reject any event group of one episode, so the label cannot recur.
- **Deliberately NOT flagged — not connections at all:**
  - **"Two and a Half Deaths"** (*CSI* S8E16): a **writers' swap** with *Two and a Half Men* — their writers wrote this episode, CSI's wrote one of theirs, and the sitcom's stars appear for one uncredited shot **as themselves**. No fictional character crosses over.
  - **"Theory of Everything"** (*CSI* S8E15): *MythBusters* hosts Jamie Hyneman and Adam Savage appear **as themselves** (background lab technicians) — a real-person cameo, not a series connection.
  - **"Sqweegel"** (*CSI* S11E4): its killer comes from Anthony Zuiker's *Level 26* **novels** — a cross-media tie-in, not another TV series.
  - **D.B. Russell joining *CSI: Cyber***: a **regular-cast migration** (Danson's character is a main cast member for Cyber's whole second season), not an episode appearance. His guest spot inside the "Seth and Apep" event above is flagged there, where it belongs.
  - **Julie Finlay in *CSI: Cyber* S2E5 "Hack E.R."**: Elisabeth Shue appears in **archive footage only**, lifted from a *CSI* episode — no appearance was performed for *Cyber*, and this pack annotates performed appearances.
  - **Original-series *regulars* in *CSI: Vegas***: *Vegas* is a direct sequel set in the same lab, and its returning originals who are **cast** — Grissom & Sara Sidle (season 1 main cast) and Catherine Willows (seasons 2–3 main cast) — are **sequel-cast continuity, not cameos**, and carry no annotation. Only the *guest-capacity* returns (Brass, Hodges, Sanders) are annotated, per Sam's 2026-11-04 ruling recorded in the cameo list above. No character from *Miami*, *NY*, *Cyber* or *Without a Trace* ever appears in *CSI: Vegas*.
  - **Same actor, different character** is not a connection (Carmine Giovinazzo played a one-scene suspect in *CSI* before starring in *NY*; Ricky Harris played a second, unrelated role in *Miami* after Disco Placid).
- **How the app uses it:** event groups drive the crossover banners, event strips, and badges in the tracker; cameo annotations are shown on the episode's page.

## 5. What the app itself fetches after install

So there are no surprises later: once installed, the app talks to **TVmaze only** (descriptions and air-date gaps for episodes already in the pack — the refresh **never** changes the watch order, adds episodes, or touches links or runtimes), plus your Roku TV on your own network. Everything in §§1–4 was gathered at build time and ships inside the universe pack.

## Credits summary

- Watch order: the **"CSI Universe Release Order"** spreadsheet (authorship not stated in the sheet), credited by title and link.
- Season/episode numbers, descriptions, and air dates: **TVmaze**, CC BY-SA.
- Prime Video links and runtimes: matched via **JustWatch** (Canada); the IDs themselves are Amazon's, hosted by the Paramount+ Amazon Channel.
- Crossover events: the franchise record as cited in §4.
- One Timeline is a fan project, not affiliated with CBS, Paramount, Warner Bros. Television, Roku, or Amazon.
