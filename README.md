# One Timeline

**Every show. One timeline.**

One Timeline is a free Android app for watching interwoven TV series the way they were meant to be watched: in **one chronological watch order** that runs across every show in a shared universe — so you always know the single correct next episode, and the app can start it playing on your TV for you.

The first universe included is **One Chicago** — *Chicago Fire*, *Chicago P.D.*, *Chicago Med*, with *Chicago Justice*, *FBI*, and *Law & Order: SVU* crossover appearances — 846 episodes in one order. Other universes (the Arrowverse is planned) will arrive as additional packs.

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

Playing an episode needs a link to that episode on Prime Video. The One Chicago universe **ships with its episode links included**. Other universes — or your own updated links file — can be added at any time: the app has an **Episode links** field in Settings (and during setup) that loads a small JSON file of links from an address you give it. The format and rules are documented in [docs/EPISODE-LINKS.md](docs/EPISODE-LINKS.md), and this repository's own copy of the Chicago links file is [onetimeline-prime-links.json](onetimeline-prime-links.json).

## Privacy

No accounts, no ads, no analytics, no tracking. Everything you watch and every setting lives on your phone. The full note is in [docs/PRIVACY.md](docs/PRIVACY.md).

## Credits

- **Watch order** is based on the community spreadsheet **"Chicago Franchise Episode Timeline (with Cameos)"**, created and maintained by its author and the contributors who keep it current — thank you. <https://docs.google.com/spreadsheets/d/1d6nnW_I3qrWUujOXi1Db2717wUKX86J4wRZdGOYPDog>
- **Episode descriptions** are provided by [TVmaze](https://www.tvmaze.com) and used under the CC BY-SA licence.
- One Timeline is a fan project. It is **not affiliated with** NBC, NBCUniversal, Wolf Entertainment, Roku, Amazon, or The CW. Show titles are used only to describe compatibility and episode data.

## Building

The app is .NET MAUI (net10.0-android). Toolchain and build commands are in [docs/BUILDING.md](docs/BUILDING.md).

## Licence

The app's source code is released under the [MIT Licence](LICENSE). Episode data carries its own credits, above — the licence covers the code, not the shows, their titles, or their data.
