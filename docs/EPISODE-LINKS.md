# Episode links

To play an episode, One Timeline needs a **streaming link** for it: the entry's ID on one of the services the app can drive on a Roku TV — **Prime Video**, **Netflix**, or **Disney+** — which the app hands to the TV. Each service has its own ID form:

| Service | The ID | Where you find it |
|---|---|---|
| Prime Video | A content ID (GTI): `amzn1.dv.gti.…` | Share → Copy link on the episode's Prime Video page |
| Netflix | The episode's **watch ID**: the number in `netflix.com/watch/<id>` | Start the episode in a browser and copy the address. (A show's own series ID only *resumes* the show — it can't open a specific episode.) |
| Disney+ | The entry's **entity/play UUID** | The code after `/browse/entity-` or `/play/` in its Disney+ address — without the `entity-` prefix |

## Where links come from

In order of precedence, per episode and per service:

1. **Your link for an episode** — pasted on the episode's own page ("Your streaming links"). Always wins over the pack's link for the same service.
2. **The universe pack's built-in links** — every universe ships with its links included in the pack itself. The One Chicago pack includes Prime Video links for **827 of its 846** episodes; the episodes without one (all of *Chicago Justice*, and the handful most recently aired) simply have no Play button. The CSI pack's links play inside Prime Video with the Paramount+ channel add-on.

An episode can carry links on several services at once. When you press Play, the app uses the **first** link — in the pack's order — whose service you have (your **My Services** selection in Settings) and whose channel is installed on your TV. If none of an episode's links is on a service you have, the episode has no Play button at all: it's a plain checklist entry, ticked off by hand.

Nothing is fetched or loaded from the network: links live in the pack, and your own links live on your device. Loading a links file from an address was an earlier design and has been removed.

## Fixing or adding a link

1. Find the episode's link or ID on the service (the table above says where, per service).
2. In One Timeline, open the episode's page and find **Your streaming links**.
3. Paste the full link (or the bare ID) in that service's section and save. The app extracts the ID and validates it against the service's form before storing it; a link in the wrong form is refused, with the reason.
4. Your link takes over immediately for that service. **Remove** in the same section deletes it and restores the pack's built-in link.

Your links are stored **on your device only**, per episode, and travel with that universe's progress exports.

## Where the built-in links came from

Chicago's Prime Video links were matched episode-by-episode against Prime Video Canada via JustWatch, and every one was verified unique. The full account — sources, method, and terms position — is in [ONE-CHICAGO-SOURCES.md](ONE-CHICAGO-SOURCES.md) (§3). The identical list is kept in this repository as [onetimeline-prime-links.json](../onetimeline-prime-links.json), with the spreadsheet credit at its top, so anyone can audit exactly which ID belongs to which episode. The CSI pack's account is in [CSI-SOURCES.md](CSI-SOURCES.md).

## If you're authoring a universe

Links are part of the universe file itself (the `links` list on each entry, with the legacy `primeContentId` form still accepted for Prime). The authoring instructions — the per-service ID forms, the multi-link priority rule, and the rule that a universe's sources must be documented before it can be added to the release — are in [CREATING-A-UNIVERSE.md](CREATING-A-UNIVERSE.md).
