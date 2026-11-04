using System.Security.Cryptography;
using System.Text;

namespace OneChicago.Services;

/// <summary>
/// "Share diagnostics" (production Decision 6, audit §3): a short
/// snapshot of the app's CURRENT state, built only when the user
/// taps the button in Settings, shown to them on a preview page
/// first, and sent only through the Android share sheet. There is
/// no always-on log in the production build and nothing is collected
/// in the background.
///
/// Redaction rules (audit §3, applied here — the single place
/// diagnostics text is produced):
///  - Prime Video IDs are never printed in full: an episode's code
///    plus the ID's first characters and a short hash of the ID.
///  - The TV's address is never printed — only whether one is set.
///  - The Roku serial number never appears (the device summary this
///    reuses already excludes it).
///  - Watched history appears as counts only, plus the one episode
///    currently up next.
///  - The whole snapshot is hard-capped at ~8 KB.
/// </summary>
public static class DiagnosticsService
{
	public const int MaxChars = 8192;

	public static async Task<string> BuildSnapshotAsync(Store store, RokuClient roku)
	{
		var sb = new StringBuilder();
		sb.AppendLine("One Timeline — diagnostics");
		sb.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm} (device time)");
		sb.AppendLine();

		// App + phone.
		sb.AppendLine($"App version: {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})");
		sb.AppendLine($"Package: {AppInfo.Current.PackageName}");
		sb.AppendLine($"Phone: {DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}");
		sb.AppendLine($"System: {DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}");
		sb.AppendLine();

		// TV: reachability and model only — never the address, never
		// the serial.
		bool addressSet = store.TvIp.Length > 0;
		sb.AppendLine(addressSet ? "TV address: set (private address, not shown)" : "TV address: not set");
		if (addressSet)
		{
			var info = await roku.GetDeviceInfoSummaryAsync();
			sb.AppendLine(info != null
				? $"TV check just now: answered — {info}"
				: "TV check just now: no answer");
		}
		sb.AppendLine();

		// Universe (Decision 1C): which pack is on screen, and how
		// many the user has included. Ids and counts only — pack
		// names are public catalogue facts.
		sb.AppendLine($"Universe: {store.ActiveUniverseId} (active) · {store.IncludedUniverseIds.Count} included");
		sb.AppendLine();

		// Dataset.
		int withDesc = store.Episodes.Count(e => !string.IsNullOrWhiteSpace(e.Description));
		sb.AppendLine($"Dataset: {store.Episodes.Count} episodes · {withDesc} with descriptions");
		sb.AppendLine($"Dataset fingerprint: {Shorten(Store.DatasetFingerprint(store.Episodes), 12)}");
		sb.AppendLine(store.DataUpdatedAt is { } upd
			? $"Data last refreshed: {upd.LocalDateTime:yyyy-MM-dd HH:mm}"
			: store.DataRefreshAttemptedAt is { } att
				? $"Data refresh attempted {att.LocalDateTime:yyyy-MM-dd HH:mm}; no successful refresh yet"
				: "Data refresh: never run");
		sb.AppendLine();

		// Progress: counts only, plus the current episode's identity.
		sb.AppendLine($"Progress: {store.WatchedCount} of {store.Episodes.Count} episodes watched");
		var next = store.UpNext;
		sb.AppendLine(next != null ? $"Up next: {next.Headline}" : "Up next: none — the watch order is complete");
		sb.AppendLine();

		// The user's own saved Prime links: count, then each episode
		// identified by code with only the ID's first characters and
		// a short hash — enough to correlate with a support report,
		// never enough to use.
		var overrides = store.Episodes
			.Where(e => store.GetPrimeOverride(e.Key) != null)
			.ToList();
		sb.AppendLine($"Your Prime Video links: {overrides.Count} saved");
		foreach (var ep in overrides.Take(25))
		{
			var id = store.GetPrimeOverride(ep.Key)!;
			sb.AppendLine($"  {ep.DisplayShow} {ep.Code} — {MaskId(id)} (id hash {ShortHash(id)})");
		}
		if (overrides.Count > 25)
			sb.AppendLine($"  …and {overrides.Count - 25} more");
		sb.AppendLine();

		// Settings summary.
		string OnOff(bool b) => b ? "on" : "off";
		sb.AppendLine("Settings:");
		sb.AppendLine($"  Autoplay: {OnOff(store.AutoplayEnabled)} · countdown {store.AutoplayCountdownSeconds} s · session limit {(store.AutoplaySessionLimit == 0 ? "none" : store.AutoplaySessionLimit.ToString())}");
		sb.AppendLine($"  Episode endings: {store.CreditsThreshold}");
		sb.AppendLine($"  Background mode: {OnOff(store.BackgroundModeEnabled)}");
#if ANDROID
		sb.AppendLine($"  Notifications allowed: {OnOff(OneChicago.Platforms.MonitorServiceController.NotificationsAllowed())}");
#endif
		sb.AppendLine($"  TV check interval: {store.PollSeconds} s · profile select: {OnOff(store.ProfileSelectEnabled)}");
		sb.AppendLine($"  Theme: {store.ThemeId} · spoiler guard: {OnOff(store.SpoilerGuardEnabled)}");
		sb.AppendLine($"  Setup completed: {OnOff(store.SetupCompleted)}");

		var text = sb.ToString().TrimEnd();
		if (text.Length > MaxChars)
			text = text.Substring(0, MaxChars - 20).TrimEnd() + "\n…(truncated)";
		return text;
	}

	/// <summary>"amzn1.dv.gti.33a0…" — the constant prefix plus the
	/// first four characters of the unique part. Never the full ID.</summary>
	private static string MaskId(string id)
	{
		const string prefix = "amzn1.dv.gti.";
		if (id.StartsWith(prefix) && id.Length > prefix.Length + 4)
			return prefix + id.Substring(prefix.Length, 4) + "…";
		return Shorten(id, 8) + "…";
	}

	/// <summary>First 8 hex characters of the ID's SHA-256 — a
	/// correlation handle for support, useless as a credential.</summary>
	private static string ShortHash(string value)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(hash).Substring(0, 8).ToLowerInvariant();
	}

	private static string Shorten(string s, int n) => s.Length <= n ? s : s.Substring(0, n);
}
