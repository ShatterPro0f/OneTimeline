# Episode links

To play an episode, One Timeline needs that episode's **Prime Video link** — specifically the episode's content ID (`amzn1.dv.gti.…`), which the app hands to the TV.

## Where links come from

In order of precedence:

1. **Your link for an episode** — pasted on the episode's own page ("Your Prime Video link"). Always wins.
2. **The universe pack's built-in links** — every universe ships with its links included in the pack itself. The One Chicago pack includes links for **827 of its 846** episodes; the episodes without one (all of *Chicago Justice*, and the handful most recently aired) simply have no Play button.

Nothing is fetched or loaded from the network: links live in the pack, and your own links live on your device. Loading a links file from an address was an earlier design and has been removed.

## Fixing or adding a link

1. In the Prime Video app or website, open the episode and use **Share → Copy link**.
2. In One Timeline, open the episode's page and find **Your Prime Video link**.
3. Paste the full link (or the bare `amzn1.dv.gti.…` ID) and save. The app extracts and validates the ID before storing it.
4. Your link takes over immediately. **Remove** on the same page deletes it and restores the pack's built-in link.

Your links are stored **on your device only**, per episode, and travel with that universe's progress exports.

## Where the built-in links came from

Chicago's links were matched episode-by-episode against Prime Video Canada via JustWatch, and every one was verified unique. The full account — sources, method, and terms position — is in [ONE-CHICAGO-SOURCES.md](ONE-CHICAGO-SOURCES.md) (§3). The identical list is kept in this repository as [onetimeline-prime-links.json](../onetimeline-prime-links.json), with the spreadsheet credit at its top, so anyone can audit exactly which ID belongs to which episode.

## If you're authoring a universe

Links are part of the universe file itself (the optional `primeContentId` field on each episode). The authoring instructions — including the rule that a universe's sources must be documented before it can be added to the release — are in [CREATING-A-UNIVERSE.md](CREATING-A-UNIVERSE.md).
