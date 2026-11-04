using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// Owns the episode dataset (embedded episodes.json) and the user's
/// local state: watched check-offs, Prime content IDs from three
/// layers — the dataset's own shipped links (the launch universe
/// ships its episodes' links; Decision 1 as amended 2026-11-03),
/// the user's fetched episode-links source layer, and the user's
/// hand-saved per-episode overrides — and TV settings. State
/// persists to progress.json in the app's private data folder; the
/// fetched source layer persists separately (sourcelinks.json).
///
/// First run (production Decision 7): NOTHING is seeded watched.
/// A fresh install starts with an honest empty progress file and the
/// setup flow asks whether the user has already watched some of the
/// shows and lets them pick a starting point. An install that
/// already has a progress file is never re-seeded and never
/// re-prompted (see SetupCompleted).
/// </summary>
public class Store
{
	private static readonly Regex GtiRegex = new(@"amzn1\.dv\.gti\.[0-9a-fA-F-]{36}", RegexOptions.Compiled);
	private static readonly Regex GtiFullRegex = new(@"\Aamzn1\.dv\.gti\.[0-9a-fA-F-]{36}\z", RegexOptions.Compiled);

	/// <summary>True only for a complete, strict Prime GTI
	/// (amzn1.dv.gti. + 36 hex/dash characters) — the same format the
	/// data-refresh pipeline enforces on IDs it extracts.</summary>
	public static bool IsValidContentId(string? id) =>
		!string.IsNullOrEmpty(id) && GtiFullRegex.IsMatch(id);

	/// <summary>
	/// Pull a bare Prime content ID out of whatever was pasted: a full
	/// share link (…/detail?gti=amzn1.dv.gti.…) or a bare GTI. Anything
	/// that contains no strict GTI yields "" — production accepts no
	/// other ID form (audit 1.4: hand-saved overrides must meet the
	/// same strict format as refreshed IDs).
	/// </summary>
	public static string NormalizeContentId(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return "";
		var m = GtiRegex.Match(raw);
		return m.Success ? m.Value : "";
	}

	/// <summary>Production ships with NO default TV address: every
	/// install starts unset and setup leads with Find My TV.</summary>
	public const string DefaultTvIp = "";

	/// <summary>
	/// The one TV-address rule, enforced at the Settings save point,
	/// inside RokuClient before any request is built, and on import —
	/// so typed, discovered, imported, and legacy stored values all
	/// face the same bar (audit 1.3): a dotted-quad IPv4 address that
	/// parses, in a private range (10/8, 172.16/12, 192.168/16) or
	/// link-local (169.254/16). Hostnames and anything carrying URL
	/// punctuation can never parse as a dotted quad, so they fail here.
	/// </summary>
	public static bool IsValidTvAddress(string? value) => TryValidateTvAddress(value, out _);

	public static bool TryValidateTvAddress(string? value, out string normalized)
	{
		normalized = "";
		if (string.IsNullOrWhiteSpace(value)) return false;
		var trimmed = value.Trim();
		// Strict dotted-quad shape first: exactly four decimal octets.
		// (IPAddress.TryParse alone also accepts shorthand forms like
		// "192.168.1" — a TV address field should never carry those.)
		var parts = trimmed.Split('.');
		if (parts.Length != 4) return false;
		foreach (var part in parts)
		{
			if (part.Length is < 1 or > 3) return false;
			foreach (var ch in part)
				if (ch is < '0' or > '9') return false;
			if (int.Parse(part) > 255) return false;
		}
		if (!IPAddress.TryParse(trimmed, out var addr)) return false;
		if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
		var b = addr.GetAddressBytes();
		bool local =
			b[0] == 10 ||
			(b[0] == 172 && b[1] is >= 16 and <= 31) ||
			(b[0] == 192 && b[1] == 168) ||
			(b[0] == 169 && b[1] == 254);
		if (!local) return false;
		normalized = addr.ToString();
		return true;
	}
	public const int DefaultPollSeconds = 30;

	/// <summary>The TV status-check interval's valid range (iteration
	/// 9, when it became a free-form Settings field). While an episode
	/// is playing the monitor checks every second regardless; this
	/// interval governs the quiet times, so its floor sits well above
	/// RokuMonitor's own 5 s floor.</summary>
	public const int MinPollSeconds = 10;
	public const int MaxPollSeconds = 300;
	public static bool IsValidPollSeconds(int seconds)
		=> seconds is >= MinPollSeconds and <= MaxPollSeconds;

	/// <summary>Parse a Settings text field into a valid poll
	/// interval: whole seconds within range, nothing else.</summary>
	public static bool TryParsePollSeconds(string? text, out int seconds)
	{
		if (int.TryParse(text?.Trim(), out var parsed) && IsValidPollSeconds(parsed))
		{
			seconds = parsed;
			return true;
		}
		seconds = DefaultPollSeconds;
		return false;
	}

	public const int PrimeChannelId = 13;

	/// <summary>The universe this build of the app tracks, stamped
	/// into every dataset file it writes (see DatasetFile). The app
	/// is expected to gain further universes as separate packs over
	/// time; the stamp is what will tell their datasets apart. This
	/// tracker loads only its own universe's data.</summary>
	public const string DatasetUniverseId = "one-chicago";

	public List<Episode> Episodes { get; private set; } = new();
	public bool Loaded { get; private set; }

	public string TvIp { get; set; } = DefaultTvIp;
	public int PollSeconds { get; set; } = DefaultPollSeconds;

	/// <summary>Identity (Episode.Key) of the last episode the guided Play
	/// sequence actually got playing on the TV, and the last playback
	/// position seen for it (fed by RokuMonitor's polling). The guided
	/// sequence checks these before sending anything, so re-tapping Play
	/// on the episode Sam is already watching can never restart it.</summary>
	public string LastStartedEpisodeKey { get; private set; } = "";
	public long LastStartedPositionMs { get; private set; }
	private long _lastPositionSavedAtMs = -1;

	/// <summary>Autoplay: when the TV shows the current episode has
	/// finished, auto-advance and play the next one. Default ON; Sam's
	/// choice persists. (Stored as bool? in the state file so a state
	/// file written before this setting existed still defaults ON.)</summary>
	public bool AutoplayEnabled { get; private set; } = true;

	/// <summary>The autoplay countdown length, in seconds — a free-form
	/// Settings field since iteration 9 (it was a fixed 5/10/15/30/60
	/// choice before; every one of those presets is inside the new
	/// range, so older state files keep their exact meaning). Default
	/// 10 s — the fixed length the countdown had before it became
	/// adjustable. Stored as int? so older state files default
	/// cleanly.</summary>
	public const int DefaultCountdownSeconds = 10;
	public const int MinCountdownSeconds = 0;
	public const int MaxCountdownSeconds = 600;
	public static bool IsValidCountdownSeconds(int seconds)
		=> seconds is >= MinCountdownSeconds and <= MaxCountdownSeconds;

	/// <summary>Parse a Settings text field into a valid countdown:
	/// whole seconds within range, nothing else. 0 is valid and means
	/// "start the next episode immediately" — AutoplayCoordinator
	/// clamps its own tick to a 1 s minimum internally.</summary>
	public static bool TryParseCountdownSeconds(string? text, out int seconds)
	{
		if (int.TryParse(text?.Trim(), out var parsed) && IsValidCountdownSeconds(parsed))
		{
			seconds = parsed;
			return true;
		}
		seconds = DefaultCountdownSeconds;
		return false;
	}

	public int AutoplayCountdownSeconds { get; private set; } = DefaultCountdownSeconds;

	/// <summary>Id of the app theme Sam picked in Settings (see
	/// ThemeService). Default "light" (Light Basic — the original
	/// shipping look). Stored as a plain string; unknown ids fall back
	/// to Light Basic when applied.</summary>
	public string ThemeId { get; private set; } = "light";

	// ---- Iteration-3 settings. Every default below reproduces the
	// ---- shipped v1.2 behaviour exactly; each is stored nullable in
	// ---- the state file so pre-existing state files default cleanly.

	/// <summary>Whether the guided Play sequence sends the profile-pick
	/// Select after a cold Prime launch. Default TRUE = the shipped
	/// behaviour (Sam's TV asks for a profile). OFF skips only that one
	/// keypress; every other step is identical.</summary>
	public bool ProfileSelectEnabled { get; private set; } = true;

	/// <summary>How completion detection treats the credits:
	/// "balanced" (default) = the shipped thresholds exactly (≥92% of
	/// listed runtime, or end-of-playback after ≥65%); "credits" = only
	/// the end-of-playback signal (after ≥65%) counts; "early" = ≥85%
	/// of listed runtime counts, end-of-playback rule unchanged.</summary>
	public const string CreditsBalanced = "balanced";
	public const string CreditsLetPlay = "credits";
	public const string CreditsEarly = "early";
	public string CreditsThreshold { get; private set; } = CreditsBalanced;

	/// <summary>Spoiler guard: hide descriptions of unwatched episodes
	/// until tapped. Default FALSE = descriptions always visible, as
	/// shipped.</summary>
	public bool SpoilerGuardEnabled { get; private set; }

	/// <summary>Autoplay session limit: stop firing automatic jumps
	/// after this many in one app session. 0 = no limit (default, the
	/// shipped behaviour).</summary>
	public static readonly int[] SessionLimitChoices = { 0, 1, 2, 3, 5 };
	public int AutoplaySessionLimit { get; private set; }

	// ---- Iteration-6 setting.

	/// <summary>Background mode: the Android foreground service keeps
	/// the TV monitor, completion detection and autoplay running while
	/// the app isn't on screen, behind a persistent notification.
	/// Default TRUE — the feature is Sam's explicit request, not a
	/// behaviour-preserving iteration-3-style addition. Stored nullable
	/// so older state files default ON.</summary>
	public bool BackgroundModeEnabled { get; private set; } = true;

	/// <summary>Whether the one-time notifications-permission
	/// explanation + system request has been shown (iteration 6). Not
	/// a user-facing setting — just the "ask once, never nag" marker.
	/// Default false.</summary>
	public bool NotificationPromptDone { get; private set; }

	// ---- First-run setup (production Decision 7) -------------------

	/// <summary>Whether the first-run setup flow has been finished
	/// or skipped. False only for a genuine fresh install (no state
	/// file existed at first load). A progress file that predates
	/// setup loads as completed — existing installs are never
	/// re-prompted. "Run setup again" in Settings re-opens the flow
	/// manually; completing or skipping it there simply re-saves
	/// this flag.</summary>
	public bool SetupCompleted { get; private set; }

	// ---- Iteration-7 data refresh timestamps.
	// Written quietly (no Changed event) by DataRefreshService: the
	// refresh runs in the background on every cold start and must not
	// make the UI rebuild for a bookkeeping write. Settings reads
	// them fresh each time it appears.

	/// <summary>When a data refresh last completed successfully —
	/// the dataset was verified current (and swapped if it changed).
	/// Null until the first successful run. Shown in Settings as
	/// "Data updated · &lt;date&gt;".</summary>
	public DateTimeOffset? DataUpdatedAt { get; private set; }

	/// <summary>When a data refresh was last attempted (whether it
	/// succeeded or not). Shown in Settings until the first success.</summary>
	public DateTimeOffset? DataRefreshAttemptedAt { get; private set; }

	// ---- Episode-links source (2026-11-03) -------------------------
	// The launch universe ships its links in the dataset; this is
	// the user's OWN additional source — an episode-links file they
	// point the app at (a future universe's file, or their own
	// updated one), fetched only on their explicit tap
	// (SourceLinksService). Device-local throughout: the address is
	// a state-file setting, the fetched layer is its own file, and
	// neither is ever part of a progress export or import.

	/// <summary>The address the user gave for their episode-links
	/// file ("" = none). Persisted in the state file so the Sources
	/// section can prefill it; fetching only ever happens on the
	/// user's tap, never from this value alone.</summary>
	public string SourceLinksUrl { get; private set; } = "";

	/// <summary>How many of the fetched file's links are live right
	/// now (recomputed against the current dataset on load).</summary>
	public int SourceLinksAppliedCount { get; private set; }

	/// <summary>How many entries the last fetch skipped (unknown
	/// episode, or no valid Prime ID in the value).</summary>
	public int SourceLinksIgnoredCount { get; private set; }

	/// <summary>When the current layer was fetched. Null until the
	/// first successful fetch.</summary>
	public DateTimeOffset? SourceLinksFetchedAt { get; private set; }

	/// <summary>Whether a fetched episode-links layer is in force.</summary>
	public bool HasSourceLinks => _sourceLinks.Count > 0;

	private readonly HashSet<string> _watched = new();
	private readonly Dictionary<string, string> _primeIds = new();

	/// <summary>Links from the user's fetched episode-links source
	/// (see SourceLinksService) — the middle precedence layer,
	/// between the dataset's shipped links and the user's hand-saved
	/// overrides. Persisted as its own file (sourcelinks.json),
	/// never inside the dataset and never inside progress.json.</summary>
	private readonly Dictionary<string, string> _sourceLinks = new();

	/// <summary>Prime IDs exactly as shipped in the dataset, captured
	/// before any user layer is applied — so progress import can
	/// restore them before applying the backup's overrides. The
	/// launch universe's dataset ships its episodes' links (Decision
	/// 1 as amended 2026-11-03); a universe whose dataset ships none
	/// simply leaves these empty.</summary>
	private readonly Dictionary<string, string> _datasetIds = new();

	public event Action? Changed;

	private string StatePath => Path.Combine(FileSystem.AppDataDirectory, "progress.json");

	/// <summary>The fetched episode-links layer's home on the device
	/// — a file of the user's own data, kept apart from both the
	/// dataset (which a refresh may swap wholesale) and the state
	/// file (whose backup format deliberately excludes it).</summary>
	private string SourceLinksPath => Path.Combine(FileSystem.AppDataDirectory, "sourcelinks.json");

	/// <summary>The on-disk form of the fetched episode-links layer:
	/// the validated links (episode key → strict GTI) plus the fetch
	/// metadata the Sources section reports.</summary>
	private class SourceLinksFileData
	{
		public string? FetchedAt { get; set; }
		public int Ignored { get; set; }
		public Dictionary<string, string> Links { get; set; } = new();
	}

	/// <summary>The refreshed dataset's home on the device (iteration
	/// 7). The APK-embedded episodes.json remains the seed; after the
	/// first successful refresh, this file is what LoadAsync reads.</summary>
	private string DataPath => Path.Combine(FileSystem.AppDataDirectory, "episodes.refreshed.json");

	private class StateFile
	{
		/// <summary>First-run setup (Decision 7): null in state files
		/// written before setup existed — LoadAsync treats those as
		/// completed, because an install with a progress file is never
		/// re-prompted. Fresh installs persist an explicit false until
		/// setup is finished or skipped.</summary>
		public bool? SetupCompleted { get; set; }
		public List<string> Watched { get; set; } = new();
		public Dictionary<string, string> PrimeIds { get; set; } = new();
		public string TvIp { get; set; } = DefaultTvIp;
		public int PollSeconds { get; set; } = DefaultPollSeconds;
		public string LastStartedEpisodeKey { get; set; } = "";
		public long LastStartedPositionMs { get; set; }
		public bool? AutoplayEnabled { get; set; }
		public int? AutoplayCountdownSeconds { get; set; }
		public string? ThemeId { get; set; }
		public bool? ProfileSelectEnabled { get; set; }
		public string? CreditsThreshold { get; set; }
		public bool? SpoilerGuardEnabled { get; set; }
		public int? AutoplaySessionLimit { get; set; }
		public bool? BackgroundModeEnabled { get; set; }
		public bool? NotificationPromptDone { get; set; }
		public string? DataUpdatedAt { get; set; }
		public string? DataRefreshAttemptedAt { get; set; }
		/// <summary>The user's episode-links source address. A
		/// state-file setting only — it is deliberately absent from
		/// the export format (see the backup section below).</summary>
		public string? SourceLinksUrl { get; set; }
	}

	/// <summary>Apply the nullable iteration-3 settings from a state
	/// file, each falling back to its v1.2-behaviour default when the
	/// file predates the setting. Shared by first load and import.</summary>
	private void ApplyOptionalSettings(StateFile state)
	{
		ProfileSelectEnabled = state.ProfileSelectEnabled ?? true;
		CreditsThreshold = state.CreditsThreshold is CreditsBalanced or CreditsLetPlay or CreditsEarly
			? state.CreditsThreshold
			: CreditsBalanced;
		SpoilerGuardEnabled = state.SpoilerGuardEnabled ?? false;
		AutoplaySessionLimit = state.AutoplaySessionLimit is int sl && SessionLimitChoices.Contains(sl)
			? sl
			: 0;
		BackgroundModeEnabled = state.BackgroundModeEnabled ?? true;
		NotificationPromptDone = state.NotificationPromptDone ?? false;
		DataUpdatedAt = ParseTimestamp(state.DataUpdatedAt);
		DataRefreshAttemptedAt = ParseTimestamp(state.DataRefreshAttemptedAt);
	}

	private static DateTimeOffset? ParseTimestamp(string? s) =>
		DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;

	private static string? FormatTimestamp(DateTimeOffset? t) =>
		t?.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Read the episode dataset: the device copy left by a
	/// successful data refresh (iteration 7) when one exists, parses,
	/// AND passes the same full validation a fresh refresh faces
	/// (audit 1.4/1.10 — the old load check was count-only), else the
	/// APK-embedded seed. A corrupt or anomalous device copy must
	/// never brick startup — it just falls back to the seed.</summary>
	private async Task<string> LoadDatasetJsonAsync()
	{
		if (File.Exists(DataPath))
		{
			try
			{
				if (new FileInfo(DataPath).Length <= BoundedRead.RefreshCapBytes)
				{
					var device = await File.ReadAllTextAsync(DataPath);
					var probe = DatasetFile.Parse(device, DatasetUniverseId);
					if (probe != null && DataRefreshService.ValidateDataset(probe)) return device;
				}
			}
			catch { /* fall through to the embedded seed */ }
		}
		using var stream = await FileSystem.OpenAppPackageFileAsync("episodes.json");
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync();
	}

	public async Task LoadAsync()
	{
		if (Loaded) return;

		var json = await LoadDatasetJsonAsync();
		Episodes = DatasetFile.Parse(json, DatasetUniverseId) ?? new();

		StateFile? state = null;
		bool stateCorrupt = false;
		if (File.Exists(StatePath))
		{
			try
			{
				state = JsonSerializer.Deserialize<StateFile>(await File.ReadAllTextAsync(StatePath));
				if (state == null) stateCorrupt = true;
			}
			catch { state = null; stateCorrupt = true; }
		}
		SourceLinksUrl = state?.SourceLinksUrl ?? "";

		if (stateCorrupt)
		{
			// Audit 1.5: a state file that fails to parse is the user's
			// only copy of their history — quarantine it (rename with a
			// timestamp, never delete, never silently overwrite) and
			// only then start fresh.
			try
			{
				var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
				File.Move(StatePath, $"{StatePath}.corrupt-{stamp}");
			}
			catch { /* quarantine is best-effort; starting fresh still beats bricking */ }
		}

		if (state == null)
		{
			// First run (Decision 7): NO seed. A fresh install starts
			// with nothing watched, and SetupCompleted stays false so
			// the setup flow is offered. The state file is still
			// written now — it persists the explicit false, so a
			// process death mid-setup re-offers setup next launch
			// instead of mistaking this for a pre-setup install.
			SetupCompleted = false;
			SaveSettings();
		}
		else
		{
			foreach (var k in state.Watched) _watched.Add(k);
			foreach (var kv in state.PrimeIds) _primeIds[kv.Key] = kv.Value;
			// A stored address that fails the production rule (typed
			// before validation existed, or carried in by an old
			// backup) is dropped to the empty setup state — the app
			// must never poll a bad address (audit 1.3).
			TvIp = TryValidateTvAddress(state.TvIp, out var validIp) ? validIp : DefaultTvIp;
			PollSeconds = IsValidPollSeconds(state.PollSeconds) ? state.PollSeconds : DefaultPollSeconds;
			LastStartedEpisodeKey = state.LastStartedEpisodeKey ?? "";
			LastStartedPositionMs = state.LastStartedPositionMs;
			_lastPositionSavedAtMs = state.LastStartedPositionMs;
			AutoplayEnabled = state.AutoplayEnabled ?? true;
			AutoplayCountdownSeconds = state.AutoplayCountdownSeconds is int cd && IsValidCountdownSeconds(cd)
				? cd
				: DefaultCountdownSeconds;
			ThemeId = string.IsNullOrWhiteSpace(state.ThemeId) ? "light" : state.ThemeId;
			// A progress file written before setup existed has no
			// flag: it belongs to an existing install, which is never
			// re-prompted — treat as completed. Only an explicitly
			// persisted false (a fresh install that hasn't finished
			// setup yet) re-offers the flow.
			SetupCompleted = state.SetupCompleted ?? true;
			ApplyOptionalSettings(state);
		}

		LoadSourceLinks();

		foreach (var ep in Episodes) _datasetIds[ep.Key] = ep.PrimeContentId;
		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);
		ApplyPrimeIds();

		Loaded = true;
		Changed?.Invoke();
	}

	/// <summary>Read the fetched episode-links layer, if one is on
	/// the device. Defensive throughout (the layer is re-fetchable,
	/// never the user's only copy of anything): an oversized,
	/// unparseable or stale-keyed file simply leaves the layer
	/// unloaded rather than costing a startup. Every surviving
	/// entry is re-checked against the CURRENT dataset's keys and
	/// the strict GTI rule, and the applied count is recomputed
	/// from what actually survived.</summary>
	private void LoadSourceLinks()
	{
		if (!File.Exists(SourceLinksPath)) return;
		try
		{
			if (new FileInfo(SourceLinksPath).Length > BoundedRead.ImportCapBytes) return;
			var data = JsonSerializer.Deserialize<SourceLinksFileData>(File.ReadAllText(SourceLinksPath));
			if (data?.Links == null) return;
			var validKeys = Episodes.Select(e => e.Key).ToHashSet();
			foreach (var kv in data.Links)
				if (validKeys.Contains(kv.Key) && IsValidContentId(kv.Value))
					_sourceLinks[kv.Key] = kv.Value;
			SourceLinksAppliedCount = _sourceLinks.Count;
			SourceLinksIgnoredCount = data.Ignored;
			SourceLinksFetchedAt = ParseTimestamp(data.FetchedAt);
		}
		catch { /* a damaged layer costs a re-fetch, never a crash */ }
	}

	public void SaveSettings() => SaveSettingsCore(notify: true);

	/// <summary>Write the state file without raising Changed — for
	/// the data-refresh bookkeeping, which must never rebuild the UI.</summary>
	private void SaveSettingsQuiet() => SaveSettingsCore(notify: false);

	private void SaveSettingsCore(bool notify)
	{
		var state = new StateFile
		{
			SetupCompleted = SetupCompleted,
			Watched = _watched.ToList(),
			PrimeIds = new Dictionary<string, string>(_primeIds),
			TvIp = TvIp,
			PollSeconds = PollSeconds,
			LastStartedEpisodeKey = LastStartedEpisodeKey,
			LastStartedPositionMs = LastStartedPositionMs,
			AutoplayEnabled = AutoplayEnabled,
			AutoplayCountdownSeconds = AutoplayCountdownSeconds,
			ThemeId = ThemeId,
			ProfileSelectEnabled = ProfileSelectEnabled,
			CreditsThreshold = CreditsThreshold,
			SpoilerGuardEnabled = SpoilerGuardEnabled,
			AutoplaySessionLimit = AutoplaySessionLimit,
			BackgroundModeEnabled = BackgroundModeEnabled,
			NotificationPromptDone = NotificationPromptDone,
			DataUpdatedAt = FormatTimestamp(DataUpdatedAt),
			DataRefreshAttemptedAt = FormatTimestamp(DataRefreshAttemptedAt),
			SourceLinksUrl = SourceLinksUrl.Length > 0 ? SourceLinksUrl : null,
		};
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
		if (notify) Changed?.Invoke();
	}

	// ---- Data refresh (iteration 7) ---------------------------------

	/// <summary>Content fingerprint of a dataset, used to tell a
	/// genuinely changed refresh from a no-op one (so an unchanged
	/// refresh doesn't rebuild the UI out from under the user). A
	/// SHA-256 hash over the canonical serialization of the whole
	/// dataset (audit 1.4) — the old aggregate (count + first/last
	/// keys + length sums) could collide across different datasets
	/// and report "unchanged" for changed data.</summary>
	public static string DatasetFingerprint(IReadOnlyList<Episode> eps)
	{
		if (eps.Count == 0) return "empty";
		var canonical = JsonSerializer.Serialize(eps);
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return Convert.ToHexString(hash);
	}

	/// <summary>Swap in a refreshed dataset (DataRefreshService has
	/// already validated it). Everything the user owns is reapplied
	/// by episode identity, exactly as first load does it: watched
	/// flags from the watched set, then the user's own link layers
	/// on top of the dataset's shipped links — fetched source layer,
	/// then hand-saved overrides (PrimeIdFor's precedence). The
	/// current episode simply re-resolves against the new list
	/// because every consumer reads UpNext off this list. Persists
	/// the refreshed dataset to the device copy LoadAsync prefers,
	/// then notifies (one ordinary UI refresh).</summary>
	public void ReplaceDataset(List<Episode> fresh)
	{
		// Serialize BEFORE the user layers are applied below: the
		// device copy must hold the dataset exactly as it shipped,
		// so _datasetIds and a future import keep meaning "dataset
		// ID", with the user's layers living only outside it.
		// Written in the envelope format, stamped with the universe.
		var pristineJson = DatasetFile.Serialize(fresh, DatasetUniverseId);
		_datasetIds.Clear();
		foreach (var ep in fresh) _datasetIds[ep.Key] = ep.PrimeContentId;
		Episodes = fresh;
		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);
		ApplyPrimeIds();
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		File.WriteAllText(DataPath, pristineJson);
		SaveSettings();
	}

	/// <summary>Sam's hand-saved Prime ID override for an episode
	/// identity, if one exists (used by the data refresh to judge ID
	/// coverage the way the app actually experiences it).</summary>
	public string? GetPrimeOverride(string key) =>
		_primeIds.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

	/// <summary>The dataset's own shipped ID for an episode identity
	/// ("" when the dataset ships none for it) — the pristine layer,
	/// never a user's. The data refresh clones rows with THIS, not
	/// the materialized effective ID, so a user's link can never be
	/// baked into a refreshed dataset.</summary>
	public string GetDatasetContentId(string key) =>
		_datasetIds.TryGetValue(key, out var id) ? id : "";

	/// <summary>The fetched episode-links source's ID for an episode
	/// identity, if the layer carries one.</summary>
	public string? GetSourceLink(string key) =>
		_sourceLinks.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

	/// <summary>THE one place an episode's playable Prime ID is
	/// resolved. Precedence: the user's hand-saved per-episode
	/// override, then a link from the user's fetched episode-links
	/// source, then the dataset's own shipped link (the launch
	/// universe ships its links — Decision 1 as amended 2026-11-03),
	/// else "". Every Play surface and the autoplay chain resolve
	/// through here; Episode.PrimeContentId is kept materialized to
	/// this same value (ApplyPrimeIds), so the two never disagree.</summary>
	public string PrimeIdFor(Episode ep)
	{
		if (_primeIds.TryGetValue(ep.Key, out var own) && !string.IsNullOrWhiteSpace(own)) return own;
		if (_sourceLinks.TryGetValue(ep.Key, out var fetched) && !string.IsNullOrWhiteSpace(fetched)) return fetched;
		return _datasetIds.TryGetValue(ep.Key, out var shipped) ? shipped : "";
	}

	/// <summary>Materialize every episode's effective Prime ID from
	/// the three layers (PrimeIdFor). Run wherever a layer changes:
	/// first load, dataset swap, fetch, clear, override save, import,
	/// start over.</summary>
	private void ApplyPrimeIds()
	{
		foreach (var ep in Episodes) ep.PrimeContentId = PrimeIdFor(ep);
	}

	/// <summary>Swap in a fully validated fetched episode-links
	/// layer (SourceLinksService validates the whole file before
	/// this is called, so the swap itself cannot half-apply).
	/// Persists the layer file atomically (temp file + move, so a
	/// crash mid-write can never leave a torn layer behind), then
	/// the address in the state file, then re-resolves every
	/// episode's effective ID.</summary>
	public void ApplySourceLinks(string url, Dictionary<string, string> links, int ignored, DateTimeOffset fetchedAt)
	{
		_sourceLinks.Clear();
		foreach (var kv in links) _sourceLinks[kv.Key] = kv.Value;
		SourceLinksUrl = url;
		SourceLinksAppliedCount = _sourceLinks.Count;
		SourceLinksIgnoredCount = ignored;
		SourceLinksFetchedAt = fetchedAt;

		var data = new SourceLinksFileData
		{
			FetchedAt = FormatTimestamp(fetchedAt),
			Ignored = ignored,
			Links = new Dictionary<string, string>(_sourceLinks),
		};
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		var tmp = SourceLinksPath + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(data));
		File.Move(tmp, SourceLinksPath, overwrite: true);

		ApplyPrimeIds();
		SaveSettings();
	}

	/// <summary>Remove the fetched episode-links layer and its
	/// address (the Sources section's Clear). Hand-saved per-episode
	/// overrides and the dataset's shipped links are untouched —
	/// episodes simply fall back to them.</summary>
	public void ClearSourceLinks()
	{
		_sourceLinks.Clear();
		SourceLinksUrl = "";
		SourceLinksAppliedCount = 0;
		SourceLinksIgnoredCount = 0;
		SourceLinksFetchedAt = null;
		if (File.Exists(SourceLinksPath)) File.Delete(SourceLinksPath);
		ApplyPrimeIds();
		SaveSettings();
	}

	/// <summary>Record a refresh attempt (persisted quietly).</summary>
	public void MarkDataRefreshAttempted(DateTimeOffset at)
	{
		DataRefreshAttemptedAt = at;
		SaveSettingsQuiet();
	}

	/// <summary>Record a successful refresh (persisted quietly —
	/// ReplaceDataset's own save carries the notification when the
	/// data actually changed).</summary>
	public void MarkDataRefreshed(DateTimeOffset at)
	{
		DataUpdatedAt = at;
		SaveSettingsQuiet();
	}

	public Episode? UpNext => Episodes.FirstOrDefault(e => !e.Watched);

	public int WatchedCount => Episodes.Count(e => e.Watched);

	public List<Episode> Upcoming(int count)
	{
		var next = UpNext;
		if (next == null) return new();
		return Episodes.Where(e => e.Order > next.Order && !e.Watched).Take(count).ToList();
	}

	public List<Episode> EventGroupOf(Episode ep)
	{
		if (ep.EventGroup == null) return new();
		return Episodes.Where(e => e.EventGroup == ep.EventGroup).OrderBy(e => e.Order).ToList();
	}

	/// <summary>Record that the first-run setup flow was finished or
	/// skipped (Decision 7), so it is never auto-offered again. The
	/// Settings "Run setup again" entry re-opens the flow manually
	/// regardless of this flag.</summary>
	public void SetSetupCompleted()
	{
		if (SetupCompleted) return;
		SetupCompleted = true;
		SaveSettings();
	}

	public void SetWatched(Episode ep, bool watched)
	{
		ep.Watched = watched;
		if (watched) _watched.Add(ep.Key); else _watched.Remove(ep.Key);
		SaveSettings();
	}

	/// <summary>Save a hand-entered Prime ID override. Production
	/// enforces the same strict amzn1.dv.gti format as the refresh
	/// pipeline (audit 1.4): a pasted share link yields its GTI and is
	/// accepted; anything with no valid GTI in it is REJECTED —
	/// nothing is stored and the caller gets false so its UI surface
	/// can tell the user plainly. An empty value clears the override
	/// and returns true.</summary>
	public bool SetPrimeContentId(Episode ep, string id)
	{
		if (string.IsNullOrWhiteSpace(id))
		{
			// Clearing an override falls back through the layers —
			// a fetched source link if one covers this episode,
			// else the dataset's shipped link, else none.
			_primeIds.Remove(ep.Key);
			ep.PrimeContentId = PrimeIdFor(ep);
			SaveSettings();
			return true;
		}
		var normalized = NormalizeContentId(id);
		if (!IsValidContentId(normalized)) return false;
		_primeIds[ep.Key] = normalized;
		ep.PrimeContentId = PrimeIdFor(ep);
		SaveSettings();
		return true;
	}

	/// <summary>Record that the guided Play sequence got this episode
	/// playing on the TV. Persisted, so the already-playing guard in
	/// GuidedPlayer survives app restarts.</summary>
	public void NoteEpisodeStarted(Episode ep, long positionMs)
	{
		LastStartedEpisodeKey = ep.Key;
		LastStartedPositionMs = positionMs;
		_lastPositionSavedAtMs = positionMs;
		SaveSettings();
	}

	/// <summary>Feed the last-seen playback position for the last-started
	/// episode (called by RokuMonitor's regular polling). Kept in memory
	/// every poll; written to disk only when it has moved by 15 s or more,
	/// so the state file isn't rewritten on every poll.</summary>
	public void NoteLastStartedPosition(long positionMs)
	{
		if (LastStartedEpisodeKey.Length == 0) return;
		LastStartedPositionMs = positionMs;
		if (Math.Abs(positionMs - _lastPositionSavedAtMs) >= 15_000)
		{
			_lastPositionSavedAtMs = positionMs;
			SaveSettings();
		}
	}

	public void SetAutoplayEnabled(bool enabled)
	{
		AutoplayEnabled = enabled;
		SaveSettings();
	}

	public void SetAutoplayCountdownSeconds(int seconds)
	{
		AutoplayCountdownSeconds = IsValidCountdownSeconds(seconds) ? seconds : DefaultCountdownSeconds;
		SaveSettings();
	}

	/// <summary>The TV status-check interval (iteration 9 gave it a
	/// Settings field). Out-of-range values fall back to the default,
	/// matching the load path.</summary>
	public void SetPollSeconds(int seconds)
	{
		PollSeconds = IsValidPollSeconds(seconds) ? seconds : DefaultPollSeconds;
		SaveSettings();
	}

	public void SetThemeId(string themeId)
	{
		ThemeId = string.IsNullOrWhiteSpace(themeId) ? "light" : themeId;
		SaveSettings();
	}

	public void SetProfileSelectEnabled(bool enabled)
	{
		ProfileSelectEnabled = enabled;
		SaveSettings();
	}

	public void SetCreditsThreshold(string mode)
	{
		CreditsThreshold = mode is CreditsBalanced or CreditsLetPlay or CreditsEarly ? mode : CreditsBalanced;
		SaveSettings();
	}

	public void SetSpoilerGuardEnabled(bool enabled)
	{
		SpoilerGuardEnabled = enabled;
		SaveSettings();
	}

	public void SetAutoplaySessionLimit(int limit)
	{
		AutoplaySessionLimit = SessionLimitChoices.Contains(limit) ? limit : 0;
		SaveSettings();
	}

	public void SetBackgroundModeEnabled(bool enabled)
	{
		BackgroundModeEnabled = enabled;
		SaveSettings();
	}

	/// <summary>Record that the one-time notifications-permission
	/// prompt has been shown, whatever Sam chose — it never nags
	/// twice. Settings keeps showing the permission state regardless.</summary>
	public void MarkNotificationPromptDone()
	{
		if (NotificationPromptDone) return;
		NotificationPromptDone = true;
		SaveSettings();
	}

	// ---- Progress backup (Settings) --------------------------------
	// Export/import/start-over are explicit-tap only; nothing here runs
	// automatically. Import replaces the watched set by episode
	// identity, the same application normal state loading uses.

	// ---- Production backup format (audit 1.5) -------------------
	// Versioned, and containing ONLY the watched list and settings:
	// no TV address, no Prime-ID overrides, no last-started position,
	// no leftover fields. The episode-links source is likewise
	// excluded — neither its address nor the fetched layer is ever
	// exported, and import never touches either. Import validates
	// the schema/version, allow-lists watched keys against the
	// current dataset, and — for legacy (pre-production) backups,
	// which carried everything — applies the strict GTI rule to any
	// content IDs and the TV address rule to any TV address field
	// it finds.

	private class ExportFile
	{
		[JsonPropertyName("format")] public string Format { get; set; } = "onechicago-progress";
		[JsonPropertyName("version")] public int Version { get; set; } = 1;
		[JsonPropertyName("watched")] public List<string> Watched { get; set; } = new();
		[JsonPropertyName("settings")] public ExportSettings Settings { get; set; } = new();
	}

	private class ExportSettings
	{
		[JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; }
		[JsonPropertyName("autoplayEnabled")] public bool AutoplayEnabled { get; set; }
		[JsonPropertyName("autoplayCountdownSeconds")] public int AutoplayCountdownSeconds { get; set; }
		[JsonPropertyName("themeId")] public string ThemeId { get; set; } = "light";
		[JsonPropertyName("profileSelectEnabled")] public bool ProfileSelectEnabled { get; set; }
		[JsonPropertyName("creditsThreshold")] public string CreditsThreshold { get; set; } = CreditsBalanced;
		[JsonPropertyName("spoilerGuardEnabled")] public bool SpoilerGuardEnabled { get; set; }
		[JsonPropertyName("autoplaySessionLimit")] public int AutoplaySessionLimit { get; set; }
		[JsonPropertyName("backgroundModeEnabled")] public bool BackgroundModeEnabled { get; set; }
	}

	/// <summary>The current progress as the production backup format
	/// (see above), for the share sheet.</summary>
	public string ExportStateJson()
	{
		var export = new ExportFile
		{
			Watched = _watched.ToList(),
			Settings = new ExportSettings
			{
				PollSeconds = PollSeconds,
				AutoplayEnabled = AutoplayEnabled,
				AutoplayCountdownSeconds = AutoplayCountdownSeconds,
				ThemeId = ThemeId,
				ProfileSelectEnabled = ProfileSelectEnabled,
				CreditsThreshold = CreditsThreshold,
				SpoilerGuardEnabled = SpoilerGuardEnabled,
				AutoplaySessionLimit = AutoplaySessionLimit,
				BackgroundModeEnabled = BackgroundModeEnabled,
			},
		};
		return JsonSerializer.Serialize(export);
	}

	/// <summary>Replace progress from a backup. Returns false — and
	/// changes nothing — if the text isn't a valid backup (schema or
	/// version wrong, oversized, or unparseable).</summary>
	public bool ImportStateJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return false;
		if (Encoding.UTF8.GetByteCount(json) > BoundedRead.ImportCapBytes) return false;

		JsonDocument doc;
		try { doc = JsonDocument.Parse(json); }
		catch { return false; }
		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return false;
			bool versioned = root.TryGetProperty("version", out _)
				|| root.TryGetProperty("format", out _);
			return versioned ? ImportVersioned(root) : ImportLegacy(json);
		}
	}

	private bool ImportVersioned(JsonElement root)
	{
		if (!root.TryGetProperty("format", out var format)
			|| format.ValueKind != JsonValueKind.String
			|| format.GetString() != "onechicago-progress") return false;
		if (!root.TryGetProperty("version", out var version)
			|| version.ValueKind != JsonValueKind.Number
			|| !version.TryGetInt32(out var v) || v != 1) return false;
		if (!root.TryGetProperty("watched", out var watchedEl)
			|| watchedEl.ValueKind != JsonValueKind.Array) return false;

		var validKeys = Episodes.Select(e => e.Key).ToHashSet();
		var watched = new HashSet<string>();
		foreach (var item in watchedEl.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { } key && validKeys.Contains(key))
				watched.Add(key);
		}

		_watched.Clear();
		foreach (var k in watched) _watched.Add(k);
		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);

		if (root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
		{
			if (s.TryGetProperty("pollSeconds", out var poll) && poll.ValueKind == JsonValueKind.Number
				&& poll.TryGetInt32(out var pollVal) && IsValidPollSeconds(pollVal))
				PollSeconds = pollVal;
			if (s.TryGetProperty("autoplayEnabled", out var ap) && (ap.ValueKind is JsonValueKind.True or JsonValueKind.False))
				AutoplayEnabled = ap.GetBoolean();
			if (s.TryGetProperty("autoplayCountdownSeconds", out var cd) && cd.ValueKind == JsonValueKind.Number
				&& cd.TryGetInt32(out var cdVal) && IsValidCountdownSeconds(cdVal))
				AutoplayCountdownSeconds = cdVal;
			if (s.TryGetProperty("themeId", out var theme) && theme.ValueKind == JsonValueKind.String
				&& theme.GetString() is { Length: > 0 and <= 50 } themeVal)
				ThemeId = themeVal;
			if (s.TryGetProperty("profileSelectEnabled", out var prof) && (prof.ValueKind is JsonValueKind.True or JsonValueKind.False))
				ProfileSelectEnabled = prof.GetBoolean();
			if (s.TryGetProperty("creditsThreshold", out var credits) && credits.ValueKind == JsonValueKind.String
				&& credits.GetString() is CreditsBalanced or CreditsLetPlay or CreditsEarly)
				CreditsThreshold = credits.GetString()!;
			if (s.TryGetProperty("spoilerGuardEnabled", out var spoiler) && (spoiler.ValueKind is JsonValueKind.True or JsonValueKind.False))
				SpoilerGuardEnabled = spoiler.GetBoolean();
			if (s.TryGetProperty("autoplaySessionLimit", out var limit) && limit.ValueKind == JsonValueKind.Number
				&& limit.TryGetInt32(out var limitVal) && SessionLimitChoices.Contains(limitVal))
				AutoplaySessionLimit = limitVal;
			if (s.TryGetProperty("backgroundModeEnabled", out var bg) && (bg.ValueKind is JsonValueKind.True or JsonValueKind.False))
				BackgroundModeEnabled = bg.GetBoolean();
		}
		SaveSettings();
		return true;
	}

	/// <summary>Legacy backups (the pre-production format was the
	/// whole state file). Watched keys are allow-listed against the
	/// current dataset; Prime-ID overrides survive only with a key in
	/// the dataset AND a strict-GTI value; a TV address is applied
	/// only if it passes the same validation as everywhere else; the
	/// last-started position is bounded to a plausible range. Any
	/// leftover fields in the old file (including the removed test-ID
	/// field) are ignored by deserialization.</summary>
	private bool ImportLegacy(string json)
	{
		StateFile? state;
		try
		{
			state = JsonSerializer.Deserialize<StateFile>(json);
		}
		catch
		{
			return false;
		}
		if (state == null || state.Watched == null) return false;

		var validKeys = Episodes.Select(e => e.Key).ToHashSet();
		_watched.Clear();
		foreach (var k in state.Watched)
			if (validKeys.Contains(k)) _watched.Add(k);
		_primeIds.Clear();
		if (state.PrimeIds != null)
			foreach (var kv in state.PrimeIds)
				if (validKeys.Contains(kv.Key) && IsValidContentId(kv.Value))
					_primeIds[kv.Key] = kv.Value;
		TvIp = TryValidateTvAddress(state.TvIp, out var validIp) ? validIp : TvIp;
		PollSeconds = IsValidPollSeconds(state.PollSeconds) ? state.PollSeconds : DefaultPollSeconds;
		LastStartedEpisodeKey = state.LastStartedEpisodeKey != null && validKeys.Contains(state.LastStartedEpisodeKey)
			? state.LastStartedEpisodeKey : "";
		LastStartedPositionMs = state.LastStartedPositionMs is >= 0 and <= 86_400_000 ? state.LastStartedPositionMs : 0;
		_lastPositionSavedAtMs = LastStartedPositionMs;
		AutoplayEnabled = state.AutoplayEnabled ?? true;
		AutoplayCountdownSeconds = state.AutoplayCountdownSeconds is int cd && IsValidCountdownSeconds(cd)
			? cd
			: DefaultCountdownSeconds;
		ThemeId = !string.IsNullOrWhiteSpace(state.ThemeId) && state.ThemeId.Length <= 50 ? state.ThemeId : "light";
		ApplyOptionalSettings(state);

		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);
		// Re-resolve every ID through the layers (dataset, fetched
		// source, the backup's overrides), mirroring first load.
		// The fetched source layer itself is not part of any backup
		// and is untouched by import.
		ApplyPrimeIds();
		SaveSettings();
		return true;
	}

	/// <summary>"Start over": wipe the state file and reproduce a
	/// first install exactly — dataset re-read, NOTHING watched
	/// (Decision 7: no seed, for anyone, ever), every setting back at
	/// its default, and setup offered again on the next appearance,
	/// exactly as a fresh install behaves.</summary>
	public async Task ClearAllAsync()
	{
		if (File.Exists(StatePath)) File.Delete(StatePath);
		// A first install has no refreshed device dataset either —
		// back to the APK-embedded seed.
		if (File.Exists(DataPath)) File.Delete(DataPath);
		// Nor a fetched episode-links layer or its address.
		if (File.Exists(SourceLinksPath)) File.Delete(SourceLinksPath);

		using var stream = await FileSystem.OpenAppPackageFileAsync("episodes.json");
		using var reader = new StreamReader(stream);
		var json = await reader.ReadToEndAsync();
		Episodes = DatasetFile.Parse(json, DatasetUniverseId) ?? new();

		_watched.Clear();
		_primeIds.Clear();
		_sourceLinks.Clear();
		SourceLinksUrl = "";
		SourceLinksAppliedCount = 0;
		SourceLinksIgnoredCount = 0;
		SourceLinksFetchedAt = null;
		TvIp = DefaultTvIp;
		PollSeconds = DefaultPollSeconds;
		LastStartedEpisodeKey = "";
		LastStartedPositionMs = 0;
		_lastPositionSavedAtMs = -1;
		AutoplayEnabled = true;
		AutoplayCountdownSeconds = DefaultCountdownSeconds;
		ThemeId = "light";
		ProfileSelectEnabled = true;
		CreditsThreshold = CreditsBalanced;
		SpoilerGuardEnabled = false;
		AutoplaySessionLimit = 0;
		BackgroundModeEnabled = true;
		NotificationPromptDone = false;
		DataUpdatedAt = null;
		DataRefreshAttemptedAt = null;
		// A first install has not done setup — start-over returns to
		// exactly that state (Decision 7).
		SetupCompleted = false;

		_datasetIds.Clear();
		foreach (var ep in Episodes) _datasetIds[ep.Key] = ep.PrimeContentId;
		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);
		ApplyPrimeIds();
		SaveSettings();
	}

	/// <summary>
	/// "Start from here": make <paramref name="ep"/> the current episode.
	/// It and everything after it become unwatched; everything before it
	/// is marked watched so the episode really is the next one up.
	/// Persists like any other progress change.
	/// </summary>
	public void StartFromHere(Episode ep)
	{
		foreach (var e in Episodes)
		{
			bool w = e.Order < ep.Order;
			e.Watched = w;
			if (w) _watched.Add(e.Key); else _watched.Remove(e.Key);
		}
		SaveSettings();
	}
}
