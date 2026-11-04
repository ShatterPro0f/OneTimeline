# Episode links

To play an episode, One Timeline needs that episode's **Prime Video link** — specifically the episode's content ID (`amzn1.dv.gti.…`), which the app hands to the TV.

## Where links come from

In order of precedence:

1. **Your link for an episode** — pasted on the episode's own page ("Your Prime Video link"). Always wins.
2. **A links file you load** — one file covering many episodes, loaded through the Episode links field (below).
3. **The universe's built-in links** — the One Chicago universe ships with its links included.

## Loading a links file

1. Host the file anywhere the phone can reach over http or https (a GitHub raw link works well).
2. In the app: **Settings → Episode links** (or the same step during setup).
3. Paste the file's address and tap **Fetch**. The app downloads the file, validates the whole thing, and only then applies it — a bad file changes nothing and tells you why.
4. The status line reports how many episode links loaded. **Clear** removes the loaded file at any time.

Links you load are stored **on your device only**. They are not included in progress exports, and importing a progress file never touches them.

## File format

```json
{
  "credit": "Optional — credit for whoever compiled the file. The app ignores this field.",
  "format": "onetimeline-prime-links",
  "version": 1,
  "universe": "one-chicago",
  "links": {
    "Fire|1|1": "amzn1.dv.gti.f6ba256c-0cf8-ca90-11ab-c5f9de93c029"
  }
}
```

Rules the app enforces:

- `format` must be `onetimeline-prime-links`, and `universe` must match the universe the app is running.
- Each key is an **episode key**: `Show|Season|Episode` (for example `Fire|1|1`, `PD|3|8`, `Med|1|2`). Keys for episodes the app doesn't know are ignored, and reported as ignored in the fetch result.
- Each value may be a bare content ID (`amzn1.dv.gti.…`) **or a full Prime Video link** — the app extracts the ID the same way it does for links pasted on an episode page. Values that don't resolve to a valid ID are ignored and counted.
- A file that yields **zero** usable links is refused.
- Downloads are capped at **2 MB**.
- Extra top-level fields (like `credit`) are ignored, so files can carry their own notes and credits.

This repository's own Chicago file — [onetimeline-prime-links.json](../onetimeline-prime-links.json) — is a working example, credit included.

## Making your own file

From the Prime Video app or website, open an episode and use **Share → Copy link**; the link contains the episode's content ID. Collect one entry per episode under its key, following the format above. A "Raw" view of a file hosted on GitHub gives you exactly the kind of address the Fetch field wants.
