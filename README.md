# One Timeline

**Every show. One timeline.**

One Timeline is a free Android app for watching interwoven TV series the way they were meant to be watched: in **one chronological watch order** that runs across every show in a shared universe — so you always know the single correct next episode, and the app can start it playing on your TV for you.

The app itself ships with **no universes built in** — universes are downloadable packs, added without an app update. Two are available now. **One Chicago** — *Chicago Fire*, *Chicago P.D.*, *Chicago Med*, with *Chicago Justice*, *FBI*, and *Law & Order: SVU* crossover appearances — 846 episodes in one order. And **CSI** — *CSI: Crime Scene Investigation*, *CSI: Miami*, *CSI: NY*, *CSI: Cyber*, and *CSI: Vegas*, plus the *Without a Trace* episode that completes a crossover — 839 episodes in one order. Setup's first step asks which packs to download (skippable — packs can be added any time in Settings, under **Get more universes**), and more universes will arrive as new packs in the same list. You can also [create your own universe](docs/CREATING-A-UNIVERSE.md).

## What it does

- **One watch order across every show** in the universe, with your progress tracked on-device
- **Crossover and multi-part events flagged clearly**, so a story that jumps between shows never catches you out
- **Up Next list** — the next few episodes in order, with descriptions and air dates
- **Play on your TV**: connect a Roku TV on your local network, and the app can open the current episode in Prime Video and start it playing — resuming where you left off, not restarting
- **Background watching (optional)**: with background mode on, the app notices when an episode ends and moves the TV on to the next episode in the timeline — even between different shows
- **Themes**, including one that follows your system light/dark setting

## Get the app

Download the APK from the [**Releases**](https://github.com/ShatterPro0f/OneTimeline/releases) page and install it on your Android phone (you may need to allow installs from your browser or Files app). First-run setup is covered in [docs/SETUP.md](docs/SETUP.md).

You will need:

- An Android phone
- For TV playback: a **Roku TV** (or Roku device) on the same Wi-Fi network, with *Control by mobile apps* enabled, and **Prime Video** signed in on it

## Episode links

Playing an episode needs a link to that episode on Prime Video. **Every universe ships with its episode links built into the pack** — One Chicago includes links for 827 of its 846 episodes, and CSI includes links for 803 of its 839 (CSI's play inside Prime Video with the **Paramount+ channel add-on**; *CSI: Cyber* has no Canadian streaming link and is tracker-only). If a link is ever wrong or missing, you can paste the correct one on the episode's own page and yours takes over. Details are in [docs/EPISODE-LINKS.md](docs/EPISODE-LINKS.md); where each pack's links came from is documented in [docs/ONE-CHICAGO-SOURCES.md](docs/ONE-CHICAGO-SOURCES.md) and [docs/CSI-SOURCES.md](docs/CSI-SOURCES.md), and Chicago's list is kept in this repository as [onetimeline-prime-links.json](onetimeline-prime-links.json) for anyone who wants to audit it.

## Privacy

No accounts, no ads, no analytics, no tracking. Everything you watch and every setting lives on your phone. The full note is in [docs/PRIVACY.md](docs/PRIVACY.md).

## Credits

- **Watch order** is based on the community spreadsheet **"Chicago Franchise Episode Timeline (with Cameos)"**, created and maintained by its author and the contributors who keep it current — thank you. <https://docs.google.com/spreadsheets/d/1d6nnW_I3qrWUujOXi1Db2717wUKX86J4wRZdGOYPDog>
- **Episode descriptions** are provided by [TVmaze](https://www.tvmaze.com) and used under the CC BY-SA licence.
- The **CSI watch order** is based on the community spreadsheet **"CSI Universe Release Order"** — its author isn't named in the sheet, but thank you to whoever keeps it. <https://docs.google.com/spreadsheets/d/1I-LQamAPqCOM9PvG4IfCadLpwRGKr9hZ56BNWkBE0_I>
- **Full provenance** for each pack — every source, how the data was obtained, and how the app uses it — is documented in [docs/ONE-CHICAGO-SOURCES.md](docs/ONE-CHICAGO-SOURCES.md) and [docs/CSI-SOURCES.md](docs/CSI-SOURCES.md). Any universe added to this project must document its sources the same way; see [docs/CREATING-A-UNIVERSE.md](docs/CREATING-A-UNIVERSE.md).
- One Timeline is a fan project. It is **not affiliated with** NBC, NBCUniversal, Wolf Entertainment, CBS, Paramount, Warner Bros. Television, Roku, Amazon, or The CW. Show titles are used only to describe compatibility and episode data.

## Building

The app is .NET MAUI (net10.0-android). Toolchain and build commands are in [docs/BUILDING.md](docs/BUILDING.md).

## Licence

The app's source code is released under the [MIT Licence](LICENSE). Episode data carries its own credits, above — the licence covers the code, not the shows, their titles, or their data.
