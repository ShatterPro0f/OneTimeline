# Privacy

**One Timeline does not collect, share, or sell any personal data.**

- **No accounts.** There is nothing to sign up for.
- **No analytics, no ads, no trackers.** The app contains no third-party SDKs of any kind.
- **Your data stays on your phone.** Your watched progress, settings, the links you save, and any links file you load are stored in the app's private storage on your device. Progress and dataset files are excluded from Android's cloud backup and device-transfer features.
- **Deleting the app deletes its data.** Uninstalling removes everything the app stored.

## Network connections the app makes

Only these, and only for the feature named:

- **Your Roku TV, on your home network** — to read what's playing and to start episodes. This is Roku's own local protocol, which is unencrypted by Roku's design and never leaves your network.
- **TVmaze (api.tvmaze.com)** — when refreshing episode descriptions and air dates. As with any web request, TVmaze's servers will see your device's IP address and the app's user agent. No account or identifier is sent.
- **An episode-links address you enter yourself** — only when you tap **Fetch** in the Episode links section, and only to the address you provided.
- **Roku's discovery service on your local network** — the standard SSDP search used to find your TV when you tap **Find My TV**.

## Diagnostics

Settings has a **Share diagnostics** action. Nothing is ever sent automatically. If you choose to use it, the app shows you the exact summary first — it contains counts and app/TV state only, with episode link IDs and your TV's address redacted — and anything you do with it goes through Android's share sheet, under your control.

## Contact

Questions and issues: use this repository's [Issues](https://github.com/ShatterPro0f/OneTimeline/issues) page.
