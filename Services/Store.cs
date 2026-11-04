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
/// local state: watched check-offs, Prime content IDs from two
/// layers — the dataset's own shipped links (the launch universe
/// ships its episodes' links; Decision 1 as amended 2026-11-03) and
/// the user's hand-saved per-episode overrides — and TV settings.
/// State persists to progress.json in the app's private data folder.
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

	// ---- Universes (Decision 1C) --------------------------------
	// A universe is a data pack: its own dataset, watch order and
	// shipped links. Everything the user owns PER universe —
	// watched check-offs, hand-saved link overrides, the
	// last-started episode, the refreshed dataset copy and its
	// timestamps — lives in that universe's UniverseData below.
	// Global settings (TV, autoplay, theme…) stay global, further
	// down. The Store's long-standing public surface (Episodes,
	// UpNext, PrimeIdFor, LastStarted…, DataUpdatedAt…) delegates
	// to the ACTIVE universe's data, which is what keeps the
	// tracker, the guided player, the monitor, the foreground
	// service and diagnostics working unchanged — and universe-
	// blind — no matter how many packs the catalogue grows to.

	/// <summary>All per-universe user data, keyed by universe id.
	/// Entries are created on demand; a universe the user has
	/// never touched simply has empty everything.</summary>
	private readonly Dictionary<string, UniverseData> _universes = new();

	/// <summary>The catalogue ids the user has chosen to include
	/// (setup's Universes step; the Settings Universes section).
	/// Resolved against the catalogue on load: unknown ids drop
	/// out, and the set can never be empty — if nothing valid
	/// remains, every built-in universe is included (the
	/// fresh-install default).</summary>
	private readonly HashSet<string> _included = new();

	/// <summary>The included universe ids, in catalogue order.</summary>
	public IReadOnlyList<string> IncludedUniverseIds =>
		UniverseCatalogue.All.Where(u => _included.Contains(u.Id)).Select(u => u.Id).ToList();

	/// <summary>How many universes are included right now.</summary>
	public int IncludedUniverseCount => _included.Count;

	/// <summary>The universe every feature is currently working
	/// on. Always an included, catalogued universe.</summary>
	public string ActiveUniverseId { get; private set; } = UniverseCatalogue.DefaultId;

	/// <summary>The active universe's catalogue entry.</summary>
	public UniverseInfo ActiveUniverse => UniverseCatalogue.Find(ActiveUniverseId) ?? UniverseCatalogue.Default;

	private UniverseData DataFor(string universeId) =>
		_universes.TryGetValue(universeId, out var data) ? data : _universes[universeId] = new UniverseData();

	private UniverseData ActiveData => DataFor(ActiveUniverseId);

	/// <summary>One universe's user-owned data. The episode list
	/// loads lazily (refreshed device copy or embedded seed) and
	/// is cached; everything else is plain state, persisted per
	/// universe in the state file (see UniverseStateFile).</summary>
	private sealed class UniverseData
	{
		public List<Episode>? Episodes;
		public readonly Dictionary<string, string> DatasetIds = new();
		public readonly HashSet<string> Watched = new();
		public readonly Dictionary<string, string> PrimeIds = new();
		public string LastStartedEpisodeKey = "";
		public long LastStartedPositionMs;
		public long LastPositionSavedAtMs = -1;
		public DateTimeOffset? DataUpdatedAt;
		public DateTimeOffset? DataRefreshAttemptedAt;
	}

	private static readonly List<Episode> EmptyEpisodes = new();

	/// <summary>The ACTIVE universe's episode list — the single
	/// surface every feature reads. Empty until the universe's
	/// dataset has loaded.</summary>
	public List<Episode> Episodes => ActiveData.Episodes ?? EmptyEpisodes;

	public bool Loaded { get; private set; }

	public string TvIp { get; set; } = DefaultTvIp;
	public int PollSeconds { get; set; } = DefaultPollSeconds;

	/// <summary>Identity (Episode.Key) of the last episode the guided Play
	/// sequence actually got playing on the TV, and the last playback
	/// position seen for it (fed by RokuMonitor's polling). The guided
	/// sequence checks these before sending anything, so re-tapping Play
	/// on the episode Sam is already watching can never restart it.
	/// PER UNIVERSE (Decision 1C): these delegate to the active
	/// universe's data — "already watching episode X" is a fact about
	/// one universe's watch order, never another's.</summary>
	public string LastStartedEpisodeKey
	{
		get => ActiveData.LastStartedEpisodeKey;
		private set => ActiveData.LastStartedEpisodeKey = value;
	}
	public long LastStartedPositionMs
	{
		get => ActiveData.LastStartedPositionMs;
		private set => ActiveData.LastStartedPositionMs = value;
	}
	private long LastPositionSavedAtMs
	{
		get => ActiveData.LastPositionSavedAtMs;
		set => ActiveData.LastPositionSavedAtMs = value;
	}

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

	/// <summary>When a data refresh last completed successfully for
	/// the ACTIVE universe — the dataset was verified current (and
	/// swapped if it changed). Null until the first successful run
	/// (and always null for a universe with no refresh mapping).
	/// Shown in Settings as "Data updated · &lt;date&gt;".</summary>
	public DateTimeOffset? DataUpdatedAt
	{
		get => ActiveData.DataUpdatedAt;
		private set => ActiveData.DataUpdatedAt = value;
	}

	/// <summary>When a data refresh was last attempted for the
	/// ACTIVE universe (whether it succeeded or not). Shown in
	/// Settings until the first success.</summary>
	public DateTimeOffset? DataRefreshAttemptedAt
	{
		get => ActiveData.DataRefreshAttemptedAt;
		private set => ActiveData.DataRefreshAttemptedAt = value;
	}

	// The active universe's working sets. These delegate so every
	// existing call site in this class keeps reading and writing
	// "the" watched set / overrides / shipped IDs — they are the
	// ACTIVE universe's now (Decision 1C).
	private HashSet<string> _watched => ActiveData.Watched;
	private Dictionary<string, string> _primeIds => ActiveData.PrimeIds;

	/// <summary>Prime IDs exactly as shipped in the ACTIVE
	/// universe's dataset, captured before any user layer is
	/// applied — so progress import can restore them before
	/// applying the backup's overrides. The launch universe's
	/// dataset ships its episodes' links (Decision 1 as amended
	/// 2026-11-03); a universe whose dataset ships none simply
	/// leaves these empty.</summary>
	private Dictionary<string, string> _datasetIds => ActiveData.DatasetIds;

	public event Action? Changed;

	private string StatePath => Path.Combine(FileSystem.AppDataDirectory, "progress.json");

	/// <summary>A universe's refreshed dataset's home on the device
	/// (per-universe since the multi-universe build — Decision 1C).
	/// The APK-embedded dataset remains the seed; after the first
	/// successful refresh for a universe, this file is what its
	/// load reads.</summary>
	private static string DataPathFor(string universeId) =>
		Path.Combine(FileSystem.AppDataDirectory, $"episodes.{universeId}.refreshed.json");

	/// <summary>Where pre-multi-universe builds kept the refreshed
	/// dataset. It is the launch universe's by definition (Chicago
	/// was the only universe those builds knew): still read as
	/// Chicago's copy until Chicago's next refresh writes the
	/// per-universe path and retires this file.</summary>
	private static string LegacyDataPath =>
		Path.Combine(FileSystem.AppDataDirectory, "episodes.refreshed.json");

	/// <summary>
	/// The state file. v2 (multi-universe, Decision 1C): the
	/// universe layer — <see cref="IncludedUniverses"/>,
	/// <see cref="ActiveUniverseId"/> and the per-universe sections
	/// in <see cref="Universes"/> — sits beside the GLOBAL settings,
	/// which stay flat exactly as before. The Watched / PrimeIds /
	/// LastStarted / refresh-timestamp fields directly below are
	/// the PRE-refactor shape, kept for reading only: a file that
	/// has them but no Universes section is a pre-refactor file,
	/// and those flat per-universe fields are the launch universe's
	/// data (Chicago was the only universe then) — LoadAsync
	/// migrates them into Chicago's section untouched. New saves
	/// never write them (suppressed when null, and they stay null).
	/// </summary>
	private class StateFile
	{
		/// <summary>First-run setup (Decision 7): null in state files
		/// written before setup existed — LoadAsync treats those as
		/// completed, because an install with a progress file is never
		/// re-prompted. Fresh installs persist an explicit false until
		/// setup is finished or skipped.</summary>
		public bool? SetupCompleted { get; set; }

		// ---- Universe layer (v2) ----
		/// <summary>The included universe ids, as persisted. Null in
		/// pre-refactor files → the fresh-install default applies
		/// (every built-in universe included).</summary>
		public List<string>? IncludedUniverses { get; set; }
		/// <summary>The active universe id; null in pre-refactor
		/// files → the launch universe.</summary>
		public string? ActiveUniverseId { get; set; }
		/// <summary>Per-universe sections, keyed by universe id. Its
		/// very presence is the v2 marker: null (absent) means a
		/// pre-refactor file whose flat fields migrate.</summary>
		public Dictionary<string, UniverseStateFile>? Universes { get; set; }

		// ---- Legacy flat per-universe fields (read-only) ----
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public List<string>? Watched { get; set; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public Dictionary<string, string>? PrimeIds { get; set; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? LastStartedEpisodeKey { get; set; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public long? LastStartedPositionMs { get; set; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? DataUpdatedAt { get; set; }
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public string? DataRefreshAttemptedAt { get; set; }

		// ---- Global settings (unchanged across the refactor) ----
		public string TvIp { get; set; } = DefaultTvIp;
		public int PollSeconds { get; set; } = DefaultPollSeconds;
		public bool? AutoplayEnabled { get; set; }
		public int? AutoplayCountdownSeconds { get; set; }
		public string? ThemeId { get; set; }
		public bool? ProfileSelectEnabled { get; set; }
		public string? CreditsThreshold { get; set; }
		public bool? SpoilerGuardEnabled { get; set; }
		public int? AutoplaySessionLimit { get; set; }
		public bool? BackgroundModeEnabled { get; set; }
		public bool? NotificationPromptDone { get; set; }
	}

	/// <summary>One universe's section of the state file: its
	/// watched set, its hand-saved link overrides, its last-started
	/// episode, and its refresh timestamps. Everything here belongs
	/// to exactly one universe and is only ever applied to it.</summary>
	private class UniverseStateFile
	{
		public List<string> Watched { get; set; } = new();
		public Dictionary<string, string> PrimeIds { get; set; } = new();
		public string LastStartedEpisodeKey { get; set; } = "";
		public long LastStartedPositionMs { get; set; }
		public string? DataUpdatedAt { get; set; }
		public string? DataRefreshAttemptedAt { get; set; }
	}

	/// <summary>Apply the nullable iteration-3 settings from a state
	/// file, each falling back to its v1.2-behaviour default when the
	/// file predates the setting. Shared by first load and import.
	/// (The refresh timestamps this method used to apply are
	/// per-universe since Decision 1C and are handled with the
	/// universe layer instead.)</summary>
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
	}

	private static DateTimeOffset? ParseTimestamp(string? s) =>
		DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;

	private static string? FormatTimestamp(DateTimeOffset? t) =>
		t?.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Read one universe's episode dataset: the device copy
	/// left by a successful data refresh (iteration 7) when one
	/// exists, parses, AND passes the same full validation a fresh
	/// refresh faces (audit 1.4/1.10 — the old load check was
	/// count-only), else the universe's APK-embedded seed. A corrupt
	/// or anomalous device copy must never brick startup — it just
	/// falls back to the seed. For the launch universe, the
	/// pre-multi-universe device-copy path is tried too (it is
	/// Chicago's copy by definition).</summary>
	private async Task<string> LoadDatasetJsonAsync(string universeId)
	{
		foreach (var path in DeviceDatasetCandidates(universeId))
		{
			if (!File.Exists(path)) continue;
			try
			{
				if (new FileInfo(path).Length <= BoundedRead.RefreshCapBytes)
				{
					var device = await File.ReadAllTextAsync(path);
					var probe = DatasetFile.Parse(device, universeId);
					if (probe != null && DataRefreshService.ValidateDataset(probe)) return device;
				}
			}
			catch { /* try the next candidate, then the embedded seed */ }
		}
		var info = UniverseCatalogue.Find(universeId);
		using var stream = await FileSystem.OpenAppPackageFileAsync(info?.DatasetResourceName ?? "episodes.json");
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync();
	}

	/// <summary>The device-copy paths a universe's load tries, in
	/// order: its own per-universe path, then (launch universe only)
	/// the pre-refactor path.</summary>
	private IEnumerable<string> DeviceDatasetCandidates(string universeId)
	{
		yield return DataPathFor(universeId);
		if (universeId == UniverseCatalogue.DefaultId) yield return LegacyDataPath;
	}

	/// <summary>Load (once) and cache a universe's episode list,
	/// materializing that universe's own watched flags, shipped IDs
	/// and effective IDs onto the rows — exactly what first load has
	/// always done for Chicago, now per universe. A pack whose
	/// dataset can't be read at all loads as empty rather than
	/// bricking startup; its tracker simply has no rows.</summary>
	private async Task<List<Episode>> LoadUniverseDatasetAsync(string universeId)
	{
		var data = DataFor(universeId);
		if (data.Episodes != null) return data.Episodes;
		List<Episode> episodes;
		try
		{
			var json = await LoadDatasetJsonAsync(universeId);
			episodes = DatasetFile.Parse(json, universeId) ?? new List<Episode>();
		}
		catch
		{
			episodes = new List<Episode>();
		}
		data.Episodes = episodes;
		data.DatasetIds.Clear();
		foreach (var ep in episodes) data.DatasetIds[ep.Key] = ep.PrimeContentId;
		foreach (var ep in episodes) ep.Watched = data.Watched.Contains(ep.Key);
		foreach (var ep in episodes) ep.PrimeContentId = PrimeIdFor(data, ep);
		return episodes;
	}

	/// <summary>How many episodes a universe's dataset holds —
	/// the count shown on its setup/Settings row. Derived from the
	/// loaded dataset, never hardcoded; loads the dataset on
	/// demand and caches it.</summary>
	public async Task<int> UniverseEpisodeCountAsync(string universeId) =>
		(await LoadUniverseDatasetAsync(universeId)).Count;

	/// <summary>Resolve the universe layer from a parsed state file
	/// (or a fresh install): every known universe's own data, the
	/// included set, and the active universe.
	///
	/// MIGRATION (Decision 1C): a state file with no Universes
	/// section predates the multi-universe build. Its flat watched
	/// list, overrides, last-started episode and refresh timestamps
	/// ARE the launch universe's — Chicago was the only universe
	/// those builds knew — and are adopted into Chicago's section
	/// untouched: nothing watched, saved, or configured is lost or
	/// reset. The included set defaults to every built-in universe
	/// (today's behaviour, preserved exactly) and the active
	/// universe is the launch universe.</summary>
	private void ResolveUniverses(StateFile? state)
	{
		_universes.Clear();
		if (state?.Universes != null)
		{
			foreach (var (id, saved) in state.Universes)
			{
				if (UniverseCatalogue.Find(id) == null || saved == null) continue;
				var data = DataFor(id);
				foreach (var k in saved.Watched) data.Watched.Add(k);
				foreach (var kv in saved.PrimeIds) data.PrimeIds[kv.Key] = kv.Value;
				data.LastStartedEpisodeKey = saved.LastStartedEpisodeKey ?? "";
				data.LastStartedPositionMs = saved.LastStartedPositionMs;
				data.LastPositionSavedAtMs = saved.LastStartedPositionMs;
				data.DataUpdatedAt = ParseTimestamp(saved.DataUpdatedAt);
				data.DataRefreshAttemptedAt = ParseTimestamp(saved.DataRefreshAttemptedAt);
			}
		}
		else if (state != null)
		{
			// Pre-refactor file → its flat per-universe fields are
			// the launch universe's data.
			var data = DataFor(UniverseCatalogue.DefaultId);
			foreach (var k in state.Watched ?? new List<string>()) data.Watched.Add(k);
			foreach (var kv in state.PrimeIds ?? new Dictionary<string, string>()) data.PrimeIds[kv.Key] = kv.Value;
			data.LastStartedEpisodeKey = state.LastStartedEpisodeKey ?? "";
			data.LastStartedPositionMs = state.LastStartedPositionMs ?? 0;
			data.LastPositionSavedAtMs = state.LastStartedPositionMs ?? 0;
			data.DataUpdatedAt = ParseTimestamp(state.DataUpdatedAt);
			data.DataRefreshAttemptedAt = ParseTimestamp(state.DataRefreshAttemptedAt);
		}

		_included.Clear();
		if (state?.IncludedUniverses != null)
			foreach (var id in state.IncludedUniverses)
				if (UniverseCatalogue.Find(id) != null) _included.Add(id);
		if (_included.Count == 0)
			foreach (var u in UniverseCatalogue.All) _included.Add(u.Id);

		ActiveUniverseId = state?.ActiveUniverseId is { } savedActive && _included.Contains(savedActive)
			? savedActive
			: IncludedUniverseIds[0];
	}

	public async Task LoadAsync()
	{
		if (Loaded) return;

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
			// Universes: every built-in included, the launch universe
			// active (ResolveUniverses' defaults).
			SetupCompleted = false;
			ResolveUniverses(state: null);
			await LoadUniverseDatasetAsync(ActiveUniverseId);
			SaveSettings();
		}
		else
		{
			// The universe layer first: it adopts the file's
			// per-universe sections — or, for a pre-refactor file,
			// migrates its flat fields into the launch universe
			// (see ResolveUniverses). The active universe's dataset
			// then materializes against that data below.
			ResolveUniverses(state);
			// A stored address that fails the production rule (typed
			// before validation existed, or carried in by an old
			// backup) is dropped to the empty setup state — the app
			// must never poll a bad address (audit 1.3).
			TvIp = TryValidateTvAddress(state.TvIp, out var validIp) ? validIp : DefaultTvIp;
			PollSeconds = IsValidPollSeconds(state.PollSeconds) ? state.PollSeconds : DefaultPollSeconds;
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
			await LoadUniverseDatasetAsync(ActiveUniverseId);
		}

		Loaded = true;
		Changed?.Invoke();
	}

	public void SaveSettings() => SaveSettingsCore(notify: true);

	/// <summary>Write the state file without raising Changed — for
	/// the data-refresh bookkeeping, which must never rebuild the UI.</summary>
	private void SaveSettingsQuiet() => SaveSettingsCore(notify: false);

	private void SaveSettingsCore(bool notify)
	{
		// v2 shape (Decision 1C): global settings flat, exactly as
		// before; per-universe data in its own sections; the
		// universe layer beside them. The pre-refactor flat
		// per-universe fields are NOT written (they stay null and
		// are suppressed) — a saved file is always the new shape.
		var universes = new Dictionary<string, UniverseStateFile>();
		foreach (var (id, data) in _universes)
		{
			universes[id] = new UniverseStateFile
			{
				Watched = data.Watched.ToList(),
				PrimeIds = new Dictionary<string, string>(data.PrimeIds),
				LastStartedEpisodeKey = data.LastStartedEpisodeKey,
				LastStartedPositionMs = data.LastStartedPositionMs,
				DataUpdatedAt = FormatTimestamp(data.DataUpdatedAt),
				DataRefreshAttemptedAt = FormatTimestamp(data.DataRefreshAttemptedAt),
			};
		}
		var state = new StateFile
		{
			SetupCompleted = SetupCompleted,
			IncludedUniverses = IncludedUniverseIds.ToList(),
			ActiveUniverseId = ActiveUniverseId,
			Universes = universes,
			TvIp = TvIp,
			PollSeconds = PollSeconds,
			AutoplayEnabled = AutoplayEnabled,
			AutoplayCountdownSeconds = AutoplayCountdownSeconds,
			ThemeId = ThemeId,
			ProfileSelectEnabled = ProfileSelectEnabled,
			CreditsThreshold = CreditsThreshold,
			SpoilerGuardEnabled = SpoilerGuardEnabled,
			AutoplaySessionLimit = AutoplaySessionLimit,
			BackgroundModeEnabled = BackgroundModeEnabled,
			NotificationPromptDone = NotificationPromptDone,
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
	/// flags from the watched set, then the user's hand-saved
	/// overrides on top of the dataset's shipped links (PrimeIdFor's
	/// precedence). The current episode simply re-resolves against
	/// the new list because every consumer reads UpNext off this
	/// list. Persists the refreshed dataset to the device copy
	/// LoadAsync prefers, then notifies (one ordinary UI refresh).</summary>
	public void ReplaceDataset(List<Episode> fresh)
	{
		// Serialize BEFORE the user layers are applied below: the
		// device copy must hold the dataset exactly as it shipped,
		// so _datasetIds and a future import keep meaning "dataset
		// ID", with the user's layers living only outside it.
		// Written in the envelope format, stamped with the ACTIVE
		// universe (a refresh only ever runs for it).
		var pristineJson = DatasetFile.Serialize(fresh, ActiveUniverseId);
		var data = ActiveData;
		_datasetIds.Clear();
		foreach (var ep in fresh) _datasetIds[ep.Key] = ep.PrimeContentId;
		data.Episodes = fresh;
		foreach (var ep in fresh) ep.Watched = _watched.Contains(ep.Key);
		ApplyPrimeIds();
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		File.WriteAllText(DataPathFor(ActiveUniverseId), pristineJson);
		// Retire the pre-multi-universe device copy for the launch
		// universe — the per-universe path supersedes it from here.
		if (ActiveUniverseId == UniverseCatalogue.DefaultId && File.Exists(LegacyDataPath))
			File.Delete(LegacyDataPath);
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

	/// <summary>THE one place an episode's playable Prime ID is
	/// resolved. Precedence: the user's hand-saved per-episode
	/// override, then the dataset's own shipped link (the launch
	/// universe ships its links — Decision 1 as amended 2026-11-03),
	/// else "". Every Play surface and the autoplay chain resolve
	/// through here; Episode.PrimeContentId is kept materialized to
	/// this same value (ApplyPrimeIds), so the two never disagree.</summary>
	public string PrimeIdFor(Episode ep) => PrimeIdFor(ActiveData, ep);

	/// <summary>PrimeIdFor against one universe's layers — dataset
	/// materialization resolves rows against THEIR universe's data,
	/// not whichever universe happens to be active.</summary>
	private static string PrimeIdFor(UniverseData data, Episode ep)
	{
		if (data.PrimeIds.TryGetValue(ep.Key, out var own) && !string.IsNullOrWhiteSpace(own)) return own;
		return data.DatasetIds.TryGetValue(ep.Key, out var shipped) ? shipped : "";
	}

	/// <summary>Materialize the ACTIVE universe's episodes'
	/// effective Prime IDs from its two layers (PrimeIdFor). Run
	/// wherever a layer changes: first load, dataset swap,
	/// override save, import, start over.</summary>
	private void ApplyPrimeIds()
	{
		var data = ActiveData;
		if (data.Episodes == null) return;
		foreach (var ep in data.Episodes) ep.PrimeContentId = PrimeIdFor(data, ep);
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

	// ---- Universe selection (Decision 1C) ------------------------

	/// <summary>Include or exclude a universe — one state, two
	/// surfaces: setup's Universes step and the Settings Universes
	/// section both call this. Including takes effect immediately
	/// (the switcher and the rows pick it up from the persisted
	/// set). Excluding never deletes the universe's progress: its
	/// section stays in the state file, and re-including restores
	/// everything exactly where it was left. Excluding the ACTIVE
	/// universe moves the active pointer to another included
	/// universe. Returns false — and changes nothing — for an
	/// unknown universe, or when the exclusion would leave nothing
	/// included: the app always has at least one universe, and the
	/// UI explains that when it happens.</summary>
	public async Task<bool> SetUniverseIncludedAsync(string universeId, bool included)
	{
		if (UniverseCatalogue.Find(universeId) == null) return false;
		if (included)
		{
			if (_included.Add(universeId)) SaveSettings();
			return true;
		}
		if (!_included.Contains(universeId)) return true;
		if (_included.Count <= 1) return false;
		_included.Remove(universeId);
		if (ActiveUniverseId == universeId)
			await SetActiveUniverseAsync(IncludedUniverseIds[0]);
		else
		{
			SaveSettings();
			Changed?.Invoke();
		}
		return true;
	}

	/// <summary>Make an included universe the active one (the
	/// tracker switcher). Its dataset loads (cached after the
	/// first time), its own progress becomes the Store's working
	/// state, and every feature re-resolves against it via
	/// Changed. Returns false — and changes nothing — for a
	/// universe that isn't included.</summary>
	public async Task<bool> SetActiveUniverseAsync(string universeId)
	{
		if (!_included.Contains(universeId)) return false;
		if (universeId == ActiveUniverseId)
		{
			await LoadUniverseDatasetAsync(universeId);
			return true;
		}
		ActiveUniverseId = universeId;
		await LoadUniverseDatasetAsync(universeId);
		SaveSettings();
		Changed?.Invoke();
		return true;
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
			// Clearing an override falls back to the dataset's
			// shipped link, if it has one, else none.
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
		LastPositionSavedAtMs = positionMs;
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
		if (Math.Abs(positionMs - LastPositionSavedAtMs) >= 15_000)
		{
			LastPositionSavedAtMs = positionMs;
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
	// no leftover fields. Import validates the schema/version,
	// allow-lists watched keys against the current dataset, and —
	// for legacy (pre-production) backups, which carried
	// everything — applies the strict GTI rule to any content IDs
	// and the TV address rule to any TV address field it finds.

	private class ExportFile
	{
		[JsonPropertyName("format")] public string Format { get; set; } = "onechicago-progress";
		[JsonPropertyName("version")] public int Version { get; set; } = 1;
		/// <summary>The universe this backup belongs to (Decision
		/// 1C). Added with the multi-universe build; v1 backups
		/// exported before it have no such field and are the launch
		/// universe's by definition. A backup only ever applies to
		/// its own universe — see ImportVersioned.</summary>
		[JsonPropertyName("universe")] public string Universe { get; set; } = "";
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

	/// <summary>The ACTIVE universe's current progress as the
	/// production backup format (see above), stamped with its
	/// universe id, for the share sheet.</summary>
	public string ExportStateJson()
	{
		var export = new ExportFile
		{
			Universe = ActiveUniverseId,
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

	/// <summary>The outcome of an import attempt: applied; not a
	/// backup at all; or a perfectly good backup that belongs to a
	/// DIFFERENT universe than the active one — refused rather than
	/// applied to the wrong watch order (Decision 1C).</summary>
	public enum ImportResult { Ok, NotABackup, WrongUniverse }

	/// <summary>Replace progress from a backup. Anything but Ok —
	/// changes nothing: the text isn't a valid backup (schema or
	/// version wrong, oversized, or unparseable), or it belongs to
	/// another universe.</summary>
	public ImportResult ImportStateJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return ImportResult.NotABackup;
		if (Encoding.UTF8.GetByteCount(json) > BoundedRead.ImportCapBytes) return ImportResult.NotABackup;

		JsonDocument doc;
		try { doc = JsonDocument.Parse(json); }
		catch { return ImportResult.NotABackup; }
		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return ImportResult.NotABackup;
			bool versioned = root.TryGetProperty("version", out _)
				|| root.TryGetProperty("format", out _);
			return versioned ? ImportVersioned(root) : ImportLegacy(json);
		}
	}

	private ImportResult ImportVersioned(JsonElement root)
	{
		if (!root.TryGetProperty("format", out var format)
			|| format.ValueKind != JsonValueKind.String
			|| format.GetString() != "onechicago-progress") return ImportResult.NotABackup;
		if (!root.TryGetProperty("version", out var version)
			|| version.ValueKind != JsonValueKind.Number
			|| !version.TryGetInt32(out var v) || v != 1) return ImportResult.NotABackup;
		if (!root.TryGetProperty("watched", out var watchedEl)
			|| watchedEl.ValueKind != JsonValueKind.Array) return ImportResult.NotABackup;

		// Which universe is this backup FOR? Backups exported by
		// multi-universe builds carry the field; v1 backups predate
		// it and are the launch universe's by definition (it was
		// the only universe they could be exported from). A backup
		// applies ONLY to its own universe — importing another
		// universe's backup here is refused, changing nothing.
		var backupUniverse = root.TryGetProperty("universe", out var universeEl)
			&& universeEl.ValueKind == JsonValueKind.String
			? universeEl.GetString()
			: UniverseCatalogue.DefaultId;
		if (backupUniverse != ActiveUniverseId) return ImportResult.WrongUniverse;

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
		return ImportResult.Ok;
	}

	/// <summary>Legacy backups (the pre-production format was the
	/// whole state file). Watched keys are allow-listed against the
	/// current dataset; Prime-ID overrides survive only with a key in
	/// the dataset AND a strict-GTI value; a TV address is applied
	/// only if it passes the same validation as everywhere else; the
	/// last-started position is bounded to a plausible range. Any
	/// leftover fields in the old file (including the removed test-ID
	/// field) are ignored by deserialization. Legacy backups predate
	/// universes entirely: they are the launch universe's, so they
	/// apply only while it is active.</summary>
	private ImportResult ImportLegacy(string json)
	{
		StateFile? state;
		try
		{
			state = JsonSerializer.Deserialize<StateFile>(json);
		}
		catch
		{
			return ImportResult.NotABackup;
		}
		if (state == null || state.Watched == null) return ImportResult.NotABackup;
		if (ActiveUniverseId != UniverseCatalogue.DefaultId) return ImportResult.WrongUniverse;

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
		long legacyPosition = state.LastStartedPositionMs ?? 0;
		LastStartedPositionMs = legacyPosition is >= 0 and <= 86_400_000 ? legacyPosition : 0;
		LastPositionSavedAtMs = LastStartedPositionMs;
		AutoplayEnabled = state.AutoplayEnabled ?? true;
		AutoplayCountdownSeconds = state.AutoplayCountdownSeconds is int cd && IsValidCountdownSeconds(cd)
			? cd
			: DefaultCountdownSeconds;
		ThemeId = !string.IsNullOrWhiteSpace(state.ThemeId) && state.ThemeId.Length <= 50 ? state.ThemeId : "light";
		ApplyOptionalSettings(state);
		// The legacy file's refresh timestamps are the launch
		// universe's (per-universe since Decision 1C; the gate
		// above guarantees the launch universe is active here).
		DataUpdatedAt = ParseTimestamp(state.DataUpdatedAt);
		DataRefreshAttemptedAt = ParseTimestamp(state.DataRefreshAttemptedAt);

		foreach (var ep in Episodes) ep.Watched = _watched.Contains(ep.Key);
		// Re-resolve every ID through the layers (dataset, then the
		// backup's overrides), mirroring first load.
		ApplyPrimeIds();
		SaveSettings();
		return ImportResult.Ok;
	}

	/// <summary>"Start over": wipe the state file and reproduce a
	/// first install exactly — dataset re-read, NOTHING watched
	/// (Decision 7: no seed, for anyone, ever), every setting back at
	/// its default, and setup offered again on the next appearance,
	/// exactly as a fresh install behaves.</summary>
	public async Task ClearAllAsync()
	{
		if (File.Exists(StatePath)) File.Delete(StatePath);
		// A first install has no refreshed device datasets either —
		// back to the APK-embedded seeds: every universe's copy
		// goes, including the pre-multi-universe universe-less one.
		if (File.Exists(LegacyDataPath)) File.Delete(LegacyDataPath);
		foreach (var u in UniverseCatalogue.All)
		{
			var path = DataPathFor(u.Id);
			if (File.Exists(path)) File.Delete(path);
		}

		// Every universe's progress goes with the state file: fresh
		// UniverseData all round, every built-in universe included,
		// the launch universe active — the fresh-install universe
		// state (Decision 1C).
		_universes.Clear();
		_included.Clear();
		foreach (var u in UniverseCatalogue.All) _included.Add(u.Id);
		ActiveUniverseId = UniverseCatalogue.DefaultId;

		TvIp = DefaultTvIp;
		PollSeconds = DefaultPollSeconds;
		AutoplayEnabled = true;
		AutoplayCountdownSeconds = DefaultCountdownSeconds;
		ThemeId = "light";
		ProfileSelectEnabled = true;
		CreditsThreshold = CreditsBalanced;
		SpoilerGuardEnabled = false;
		AutoplaySessionLimit = 0;
		BackgroundModeEnabled = true;
		NotificationPromptDone = false;
		// A first install has not done setup — start-over returns to
		// exactly that state (Decision 7).
		SetupCompleted = false;

		// Reload the (now active) launch universe from its seed;
		// the loader materializes its empty watched set and shipped
		// IDs onto the rows.
		await LoadUniverseDatasetAsync(ActiveUniverseId);
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
