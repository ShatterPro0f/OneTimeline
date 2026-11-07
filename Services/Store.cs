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

	/// <summary>The universe ids the user has chosen to include
	/// (setup's Universes step; the Settings Universes section) —
	/// the WANTED set, persisted verbatim as the state file's
	/// IncludedUniverses. Since v2.1.0 a wanted universe is only
	/// LIVE while its pack is installed (registered in the
	/// catalogue): a wanted id with no pack on the device is
	/// PENDING — the mark of an install that upgraded from a
	/// pre-download build — and restores automatically when its
	/// dataset downloads (PackService.RestorePendingAsync). A
	/// fresh install wants nothing: setup's first step is the
	/// pack picker (the project ruling, 2026-11-04).</summary>
	private readonly List<string> _wanted = new();

	/// <summary>The packs installed on this device, in install
	/// order — the registry the state file persists and startup
	/// re-registers the catalogue from.</summary>
	private readonly List<InstalledPackRecord> _downloads = new();

	/// <summary>State sections for universes whose packs are NOT
	/// currently installed, kept verbatim across saves: a removed
	/// or not-yet-restored universe's progress is never dropped
	/// just because its pack is absent — when the pack installs
	/// again, its section is adopted back untouched.</summary>
	private readonly Dictionary<string, UniverseStateFile> _dormantSections = new();

	/// <summary>The active universe the state file asked for,
	/// remembered even while its pack is still pending: when
	/// that pack lands, it claims the active pointer (see
	/// InstallPackAsync).</summary>
	private string _preferredActiveId = "";

	/// <summary>The packs installed on this device.</summary>
	public IReadOnlyList<InstalledPackRecord> InstalledPacks => _downloads;

	/// <summary>One installed pack's registry record, or null.</summary>
	public InstalledPackRecord? FindInstalledPack(string id) =>
		_downloads.FirstOrDefault(p => p.Id == id);

	/// <summary>The LIVE included universe ids, in catalogue
	/// order: wanted AND installed. This is the set every feature
	/// reads — the switcher, the selection UI, the tracker.</summary>
	public IReadOnlyList<string> IncludedUniverseIds =>
		UniverseCatalogue.All.Where(u => _wanted.Contains(u.Id)).Select(u => u.Id).ToList();

	/// <summary>How many universes are live-included right now.</summary>
	public int IncludedUniverseCount => IncludedUniverseIds.Count;

	/// <summary>Wanted-but-not-installed universe ids, in wanted
	/// order: the pending restores. The pack manager shows them
	/// as "needs download"; the background restore fetches them.</summary>
	public IReadOnlyList<string> PendingRestoreIds =>
		_wanted.Where(id => UniverseCatalogue.Find(id) == null).ToList();

	/// <summary>The universe every feature is currently working
	/// on: always a live-included universe, or "" when none is —
	/// a fresh install before its first download, or an upgrade
	/// whose packs haven't restored yet. Every consumer already
	/// tolerates an empty episode list; "" keeps them there.</summary>
	public string ActiveUniverseId { get; private set; } = "";

	/// <summary>The active universe's catalogue entry, or null
	/// when no universe is active.</summary>
	public UniverseInfo? ActiveUniverse => UniverseCatalogue.Find(ActiveUniverseId);

	private UniverseData DataFor(string universeId) =>
		_universes.TryGetValue(universeId, out var data) ? data : _universes[universeId] = new UniverseData();

	/// <summary>Stand-in data for "no active universe": reads as
	/// empty, absorbs writes harmlessly, and is never persisted
	/// (it never enters _universes).</summary>
	private static readonly UniverseData NoUniverseData = new();

	private UniverseData ActiveData =>
		ActiveUniverseId.Length == 0 ? NoUniverseData : DataFor(ActiveUniverseId);

	/// <summary>One universe's user-owned data. The episode list
	/// loads lazily (refreshed device copy or the installed
	/// pack file) and
	/// is cached; everything else is plain state, persisted per
	/// universe in the state file (see UniverseStateFile).</summary>
	private sealed class UniverseData
	{
		public List<Episode>? Episodes;
		public readonly Dictionary<string, string> DatasetIds = new();
		public readonly Dictionary<string, List<EpisodeLink>> DatasetLinks = new();
		public readonly HashSet<string> Watched = new();
		/// <summary>The user's hand-saved link overrides, per
		/// episode and platform (Decision 1G generalizes the old
		/// Prime-only overrides): episode key → platform signifier
		/// → content ID. An override replaces the dataset's link
		/// for the SAME platform; it is the top precedence layer.</summary>
		public readonly Dictionary<string, Dictionary<string, string>> LinkOverrides = new();
		/// <summary>The content categories the user has switched
		/// OFF in this universe (2026-11-04): their entries leave
		/// the tracker and Up Next, progress untouched — the same
		/// non-destructive precedent as universe exclusion.</summary>
		public readonly HashSet<string> HiddenCategories = new();
		public string LastStartedEpisodeKey = "";
		public string LastStartedPlatform = "";
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
	/// on the episode the user is already watching can never restart it.
	/// PER UNIVERSE (Decision 1C): these delegate to the active
	/// universe's data — "already watching episode X" is a fact about
	/// one universe's watch order, never another's.</summary>
	public string LastStartedEpisodeKey
	{
		get => ActiveData.LastStartedEpisodeKey;
		private set => ActiveData.LastStartedEpisodeKey = value;
	}

	/// <summary>Which platform the last-started episode was
	/// started on (a PlatformCatalog signifier; "" when unknown —
	/// everything recorded before Decision 1G). Playback is only
	/// ever attributed to an episode on a platform that could be
	/// playing it (see MatchedPlatform).</summary>
	public string LastStartedPlatform
	{
		get => ActiveData.LastStartedPlatform;
		private set => ActiveData.LastStartedPlatform = value;
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
	/// finished, auto-advance and play the next one. Default ON; the user's
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

	/// <summary>Id of the app theme the user picked in Settings (see
	/// ThemeService). Default "light" (Light Basic — the original
	/// shipping look). Stored as a plain string; unknown ids fall back
	/// to Light Basic when applied.</summary>
	public string ThemeId { get; private set; } = "light";

	// ---- Iteration-3 settings. Every default below reproduces the
	// ---- shipped v1.2 behaviour exactly; each is stored nullable in
	// ---- the state file so pre-existing state files default cleanly.

	/// <summary>Whether the guided Play sequence sends the profile-pick
	/// Select after a cold Prime launch. Default TRUE = the shipped
	/// behaviour (the test TV asks for a profile). OFF skips only that one
	/// keypress; every other step is identical.</summary>
	public bool ProfileSelectEnabled { get; private set; } = true;

	// ---- Settings page groups (v2.3.0). The Settings page is
	// ---- five collapsible groups; the Store owns only the ids
	// ---- and the persisted open-set — the page owns the layout.

	/// <summary>The Settings page's group ids, in page order.
	/// The page renders one collapsible group per id.</summary>
	public const string SettingsGroupTv = "tv";
	public const string SettingsGroupUniverses = "universes";
	public const string SettingsGroupPlayback = "playback";
	public const string SettingsGroupAppearance = "appearance";
	public const string SettingsGroupSetup = "setup";
	public static readonly string[] SettingsGroups =
		{ SettingsGroupTv, SettingsGroupUniverses, SettingsGroupPlayback, SettingsGroupAppearance, SettingsGroupSetup };

	private readonly HashSet<string> _openSettingsGroups = new() { SettingsGroupTv };

	/// <summary>Whether a Settings group is currently expanded.
	/// Default: TV only — the page opens on the TV controls with
	/// every other group collapsed. The set is stored as a list
	/// in the state file so the page looks the way the user left it;
	/// a state file predating the field (null) falls back to the
	/// TV-only default, while an explicitly empty list is
	/// honoured — he closed them all. Unknown ids (a newer
	/// build's groups in an older build's hands) are dropped.</summary>
	public bool IsSettingsGroupOpen(string groupId) => _openSettingsGroups.Contains(groupId);

	/// <summary>How completion detection treats the credits:
	/// "balanced" (default) = the episode counts as finished when
	/// the position reaches its listed end — the credits start —
	/// or on end-of-playback after ≥65%; "credits" = only the
	/// end-of-playback signal (after ≥65%) counts. The iteration-3
	/// "early" mode was removed in v2.3.1 (the project ruling): it is
	/// no longer a valid value anywhere, so a state file or backup
	/// still carrying "early" loads as "balanced" — the setting
	/// migrates, nothing else is touched.</summary>
	public const string CreditsBalanced = "balanced";
	public const string CreditsLetPlay = "credits";
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
	/// Default TRUE — the feature is an explicit project requirement, not a
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
	private Dictionary<string, Dictionary<string, string>> _linkOverrides => ActiveData.LinkOverrides;

	/// <summary>Prime IDs exactly as shipped in the ACTIVE
	/// universe's dataset, captured before any user layer is
	/// applied — so progress import can restore them before
	/// applying the backup's overrides. The launch universe's
	/// dataset ships its episodes' links (Decision 1 as amended
	/// 2026-11-03); a universe whose dataset ships none simply
	/// leaves these empty.</summary>
	private Dictionary<string, string> _datasetIds => ActiveData.DatasetIds;

	/// <summary>All shipped links exactly as in the ACTIVE
	/// universe's dataset (Decision 1G's pristine layer, the
	/// multi-platform sibling of _datasetIds): episode key →
	/// the dataset's own links, in the author's priority order.</summary>
	private Dictionary<string, List<EpisodeLink>> _datasetLinks => ActiveData.DatasetLinks;

	// ---- TV channel knowledge (Decision 1G) --------------------
	// Which ECP channel id each platform's channel has ON THIS
	// TV, discovered from /query/apps and cached here (global
	// state — channels are a fact about the TV, not a universe).
	// Channel ids vary per TV and per install, so nothing may
	// hardcode them except Prime's long-standing 13, which stays
	// as the fallback when discovery hasn't run.

	private readonly Dictionary<string, string> _platformChannels = new();

	/// <summary>The cached ECP channel id for a platform on this
	/// TV, or null when it isn't known (never discovered, or the
	/// channel isn't installed). Prime falls back to its proven
	/// channel 13 when unknown.</summary>
	public string? ChannelIdForPlatform(string platformSignifier)
	{
		if (_platformChannels.TryGetValue(platformSignifier, out var id) && !string.IsNullOrWhiteSpace(id))
			return id;
		return platformSignifier == PlatformCatalog.PrimeSignifier ? PrimeChannelId.ToString() : null;
	}

	/// <summary>Whether a platform's channel is KNOWN to be
	/// installed on this TV (its id is in the discovered cache).
	/// Prime counts as known only when discovered like the
	/// others — ChannelIdForPlatform's 13 is a play-time
	/// fallback, not knowledge.</summary>
	public bool PlatformChannelKnown(string platformSignifier) =>
		_platformChannels.TryGetValue(platformSignifier, out var id) && !string.IsNullOrWhiteSpace(id);

	/// <summary>The signifiers of every platform whose channel is
	/// known installed on this TV.</summary>
	public IReadOnlySet<string> KnownInstalledPlatforms =>
		_platformChannels.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToHashSet();

	/// <summary>Record (or forget, with an empty id) a platform's
	/// channel id on this TV. Persists like any other setting
	/// unless told not to (discovery batches its save).</summary>
	public void SetPlatformChannel(string platformSignifier, string? channelId, bool save = true)
	{
		if (PlatformCatalog.Find(platformSignifier) == null) return;
		if (string.IsNullOrWhiteSpace(channelId))
			_platformChannels.Remove(platformSignifier);
		else
			_platformChannels[platformSignifier] = channelId.Trim();
		if (save) SaveSettings();
	}

	// ---- My Services (the project ruling, 2026-11-04) ----------------
	// Which of the three streaming services the user actually
	// HAS, picked in setup and mirrored in Settings. Global state
	// like the TV address — a fact about the household, not a
	// universe. Link choice (GuidedPlayer.ResolveTargetAsync)
	// only ever plays a link whose platform is selected here AND
	// whose channel is on the TV; an entry whose links all sit on
	// unselected services gets no Watch button at all and behaves
	// as a pure checklist entry. The default is ALL THREE
	// selected, so installs predating the setting behave exactly
	// as before.

	private readonly HashSet<string> _myServices =
		PlatformCatalog.All.Select(p => p.Signifier).ToHashSet();

	/// <summary>The services the user has, by signifier. Never
	/// null; an empty set is a real choice (tracker-only use),
	/// not a missing value.</summary>
	public IReadOnlySet<string> MyServices => _myServices;

	/// <summary>Whether the user has the given service.</summary>
	public bool HasService(string platformSignifier) => _myServices.Contains(platformSignifier);

	/// <summary>Select or deselect one of My Services. Persists
	/// like any other setting. Unknown signifiers are ignored.</summary>
	public void SetServiceSelected(string platformSignifier, bool selected)
	{
		if (PlatformCatalog.Find(platformSignifier) == null) return;
		if (selected) _myServices.Add(platformSignifier);
		else _myServices.Remove(platformSignifier);
		SaveSettings();
	}

	public event Action? Changed;

	private string StatePath => Path.Combine(FileSystem.AppDataDirectory, "progress.json");

	/// <summary>A universe's refreshed dataset's home on the device
	/// (per-universe since the multi-universe build — Decision 1C).
	/// The installed pack file remains the base; after the first
	/// successful refresh for a universe, this file is what its
	/// load reads.</summary>
	private static string DataPathFor(string universeId) =>
		Path.Combine(FileSystem.AppDataDirectory, $"episodes.{universeId}.refreshed.json");

	/// <summary>An installed pack's dataset file name in app data
	/// — derived from the universe id, never taken from remote
	/// data, so a hostile index can never steer a write.</summary>
	public static string PackFileNameFor(string universeId) => $"episodes.{universeId}.pack.json";

	/// <summary>Where a universe's installed pack file lives.</summary>
	private static string PackPathFor(string universeId) =>
		Path.Combine(FileSystem.AppDataDirectory, PackFileNameFor(universeId));

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
		/// (fresh installs: nothing — setup asks).</summary>
		public List<string>? IncludedUniverses { get; set; }
		/// <summary>The active universe id; null in pre-refactor
		/// files → the launch universe.</summary>
		public string? ActiveUniverseId { get; set; }
		/// <summary>Per-universe sections, keyed by universe id. Its
		/// very presence is the v2 marker: null (absent) means a
		/// pre-refactor file whose flat fields migrate.</summary>
		public Dictionary<string, UniverseStateFile>? Universes { get; set; }

		/// <summary>The installed-pack registry (v2.1.0, Decision
		/// 1J): which universe packs are on this device, at which
		/// data version, verified against which SHA-256. Null in
		/// every state file written before downloadable packs —
		/// such an install has NO packs on the device in the new
		/// sense (its universes shipped inside the old app), so
		/// its wanted universes start out pending restore, never
		/// lost (ResolveUniverses + PackService).</summary>
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public List<InstalledPackStateFile>? InstalledPacks { get; set; }

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
		/// <summary>Discovered ECP channel ids per platform
		/// signifier (Decision 1G). Null in state files written
		/// before the multi-platform build → nothing is known yet
		/// and discovery fills it in on the first Play.</summary>
		public Dictionary<string, string>? PlatformChannels { get; set; }
		/// <summary>My Services (2026-11-04): the signifiers of
		/// the streaming services the user has. Null in state
		/// files written before the setting existed → all three
		/// selected (the pre-setting behaviour). An explicitly
		/// empty list is honoured: the user has none of them.</summary>
		public List<string>? MyServices { get; set; }
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
		/// <summary>The Settings page groups left expanded
		/// (v2.3.0). Null in state files written before the
		/// grouping existed → the TV-only default; an empty
		/// list is honoured as "all closed" — the two must
		/// never blur (the My Services rule).</summary>
		public List<string>? SettingsOpenGroups { get; set; }
	}

	/// <summary>One installed pack's registry entry in the state
	/// file (v2.1.0). Registration metadata mirrors the pack's
	/// envelope — the envelope in the pack FILE is authoritative
	/// at registration time; these fields are the durable record
	/// of what was installed and verified.</summary>
	private class InstalledPackStateFile
	{
		public string Id { get; set; } = "";
		public int DataVersion { get; set; } = 1;
		public string? Sha256 { get; set; }
		public string? FileName { get; set; }
		public string? DisplayName { get; set; }
		public string? ShowsLine { get; set; }
	}

	/// <summary>One universe's section of the state file: its
	/// watched set, its hand-saved link overrides, its last-started
	/// episode, and its refresh timestamps. Everything here belongs
	/// to exactly one universe and is only ever applied to it.
	///
	/// Link overrides (Decision 1G): <see cref="LinkOverrides"/>
	/// is the current shape (episode key → platform signifier →
	/// ID). <see cref="PrimeIds"/> is the pre-1G shape, kept for
	/// READING: a section with no LinkOverrides field adopts its
	/// PrimeIds as prime-platform overrides, untouched. New saves
	/// write LinkOverrides in full AND PrimeIds as its prime
	/// subset, so a downgrade to the previous build keeps the
	/// user's Prime overrides.</summary>
	private class UniverseStateFile
	{
		public List<string> Watched { get; set; } = new();
		public Dictionary<string, string>? PrimeIds { get; set; } = new();
		public Dictionary<string, Dictionary<string, string>>? LinkOverrides { get; set; }
		/// <summary>The categories switched off in this universe
		/// (2026-11-04). Null or absent in sections written before
		/// the setting existed → nothing hidden.</summary>
		public List<string>? HiddenCategories { get; set; }
		public string LastStartedEpisodeKey { get; set; } = "";
		public string? LastStartedPlatform { get; set; }
		public long LastStartedPositionMs { get; set; }
		public string? DataUpdatedAt { get; set; }
		public string? DataRefreshAttemptedAt { get; set; }
	}

	/// <summary>Adopt one saved section's link overrides into a
	/// universe's data (Decision 1G): the current LinkOverrides
	/// shape when the section carries it; otherwise its legacy
	/// PrimeIds, which ARE prime-platform overrides.</summary>
	private static void AdoptLinkOverrides(UniverseData data, Dictionary<string, Dictionary<string, string>>? linkOverrides, Dictionary<string, string>? primeIds)
	{
		data.LinkOverrides.Clear();
		if (linkOverrides != null)
		{
			foreach (var (key, perPlatform) in linkOverrides)
			{
				if (perPlatform == null) continue;
				foreach (var (platform, id) in perPlatform)
				{
					if (PlatformCatalog.Find(platform) == null || string.IsNullOrWhiteSpace(id)) continue;
					if (!data.LinkOverrides.TryGetValue(key, out var dict))
						data.LinkOverrides[key] = dict = new Dictionary<string, string>();
					dict[PlatformCatalog.Find(platform)!.Signifier] = id;
				}
			}
			return;
		}
		if (primeIds != null)
		{
			foreach (var (key, id) in primeIds)
			{
				if (string.IsNullOrWhiteSpace(id)) continue;
				data.LinkOverrides[key] = new Dictionary<string, string>
				{
					[PlatformCatalog.PrimeSignifier] = id,
				};
			}
		}
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
		CreditsThreshold = state.CreditsThreshold is CreditsBalanced or CreditsLetPlay
			? state.CreditsThreshold
			: CreditsBalanced;
		SpoilerGuardEnabled = state.SpoilerGuardEnabled ?? false;
		AutoplaySessionLimit = state.AutoplaySessionLimit is int sl && SessionLimitChoices.Contains(sl)
			? sl
			: 0;
		BackgroundModeEnabled = state.BackgroundModeEnabled ?? true;
		NotificationPromptDone = state.NotificationPromptDone ?? false;
		// Settings groups (v2.3.0): adopt the saved open-set,
		// dropping ids this build doesn't render; a file that
		// predates the field gets the TV-only default.
		_openSettingsGroups.Clear();
		if (state.SettingsOpenGroups != null)
		{
			foreach (var g in state.SettingsOpenGroups)
				if (SettingsGroups.Contains(g)) _openSettingsGroups.Add(g);
		}
		else
		{
			_openSettingsGroups.Add(SettingsGroupTv);
		}
	}

	private static DateTimeOffset? ParseTimestamp(string? s) =>
		DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
			System.Globalization.DateTimeStyles.RoundtripKind, out var dto) ? dto : null;

	private static string? FormatTimestamp(DateTimeOffset? t) =>
		t?.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Read one universe's episode dataset. Since
	/// v2.1.0 every dataset lives on the device (nothing is
	/// embedded): first the copy left by a successful data refresh
	/// (iteration 7) when one exists, parses, AND passes the same
	/// full validation a fresh refresh faces (audit 1.4/1.10 —
	/// the old load check was count-only); then the installed
	/// pack file itself, gated on parsing as this universe's
	/// dataset (the full validation ran at install time — the
	/// network boundary — so a file the app installed is trusted
	/// to the same standard the old embedded seeds were). A
	/// corrupt or anomalous copy must never brick startup — the
	/// universe simply loads empty. For the launch universe, the
	/// pre-multi-universe refreshed-copy path is tried too (it is
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
			catch { /* try the next candidate, then the pack file */ }
		}
		var packPath = PackPathFor(universeId);
		if (File.Exists(packPath))
		{
			try
			{
				if (new FileInfo(packPath).Length <= BoundedRead.RefreshCapBytes)
				{
					var pack = await File.ReadAllTextAsync(packPath);
					if (DatasetFile.Parse(pack, universeId) != null) return pack;
				}
			}
			catch { /* fall through: the universe loads empty */ }
		}
		return "";
	}

	/// <summary>The refreshed-copy paths a universe's load tries,
	/// in order: its own per-universe path, then (launch universe
	/// only) the pre-refactor path.</summary>
	private IEnumerable<string> DeviceDatasetCandidates(string universeId)
	{
		yield return DataPathFor(universeId);
		if (universeId == UniverseCatalogue.LegacyUniverseId) yield return LegacyDataPath;
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
		data.DatasetLinks.Clear();
		foreach (var ep in episodes)
		{
			// The rows arrive link-normalized (DatasetFile.Parse):
			// capture the dataset's pristine layers — its prime ID
			// and its full links — before the user layers apply.
			data.DatasetIds[ep.Key] = ep.PrimeContentId;
			data.DatasetLinks[ep.Key] = ep.Links
				.Select(l => new EpisodeLink { Platform = l.Platform, Id = l.Id }).ToList();
		}
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

	/// <summary>Adopt one saved per-universe section into live
	/// data — the single adoption path: used at load for
	/// universes whose packs are installed, and at install time
	/// when a pack's dormant section comes back to life (its
	/// progress exactly where it was left).</summary>
	private void AdoptSection(string id, UniverseStateFile saved)
	{
		var data = DataFor(id);
		foreach (var k in saved.Watched) data.Watched.Add(k);
		// Link overrides: the current per-platform shape, or a
		// pre-1G section's PrimeIds adopted as prime overrides —
		// either way nothing is lost.
		AdoptLinkOverrides(data, saved.LinkOverrides, saved.PrimeIds);
		// Hidden categories (2026-11-04): known values only; a
		// section predating the setting has none.
		if (saved.HiddenCategories != null)
			foreach (var c in saved.HiddenCategories)
				if (EpisodeCategories.IsKnown(c))
					data.HiddenCategories.Add(c.Trim().ToLowerInvariant());
		data.LastStartedEpisodeKey = saved.LastStartedEpisodeKey ?? "";
		data.LastStartedPlatform = PlatformCatalog.Find(saved.LastStartedPlatform)?.Signifier ?? "";
		data.LastStartedPositionMs = saved.LastStartedPositionMs;
		data.LastPositionSavedAtMs = saved.LastStartedPositionMs;
		data.DataUpdatedAt = ParseTimestamp(saved.DataUpdatedAt);
		data.DataRefreshAttemptedAt = ParseTimestamp(saved.DataRefreshAttemptedAt);
	}

	/// <summary>Register the catalogue from the state file's
	/// installed-pack registry (v2.1.0): every recorded pack's
	/// file is re-validated against its envelope (universe stamp,
	/// self-declared name, parseable episodes) and registered
	/// from that envelope — the one registration path, identical
	/// for every pack. A pack whose file is missing just doesn't
	/// register (its registry entry drops; its progress section,
	/// if any, goes dormant and its wanted id stays pending). A
	/// CORRUPT file is quarantined like corrupt progress —
	/// renamed, never deleted, never fatal — and the universe
	/// doesn't register.</summary>
	private void RegisterInstalledDownloads(List<InstalledPackStateFile>? saved)
	{
		_downloads.Clear();
		if (saved == null) return;
		foreach (var record in saved)
		{
			if (record == null || !PackIndexParse.IsValidPackId(record.Id)) continue;
			if (_downloads.Any(p => p.Id == record.Id)) continue;
			var info = TryRegisterPackFile(record.Id);
			if (info == null) continue;
			_downloads.Add(new InstalledPackRecord
			{
				Id = record.Id,
				DataVersion = record.DataVersion >= 1 ? record.DataVersion : 1,
				Sha256 = record.Sha256 ?? "",
				FileName = PackFileNameFor(record.Id),
				DisplayName = info.DisplayName,
				ShowsLine = info.ShowsLine,
			});
		}
	}

	/// <summary>Validate one on-device pack file and register its
	/// universe from its envelope. Returns the registered entry,
	/// or null when the pack can't register: no file (null,
	/// quietly) or a file that fails validation (quarantined).</summary>
	private UniverseInfo? TryRegisterPackFile(string universeId)
	{
		var path = PackPathFor(universeId);
		try
		{
			if (!File.Exists(path)) return null;
			if (new FileInfo(path).Length > BoundedRead.RefreshCapBytes)
			{
				QuarantinePackFile(path);
				return null;
			}
			var envelope = DatasetFile.ParseEnvelope(File.ReadAllText(path));
			if (envelope == null || envelope.WasBareArray
				|| !string.Equals(envelope.Universe, universeId, StringComparison.Ordinal)
				|| string.IsNullOrWhiteSpace(envelope.DisplayName))
			{
				QuarantinePackFile(path);
				return null;
			}
			var info = UniverseCatalogue.InfoFromEnvelope(envelope, PackFileNameFor(universeId));
			UniverseCatalogue.Register(info);
			return info;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Quarantine a bad pack file the way corrupt
	/// progress is quarantined (audit 1.5): rename with a
	/// timestamp — never delete, never silently overwrite.</summary>
	private static void QuarantinePackFile(string path)
	{
		try
		{
			var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
			File.Move(path, $"{path}.corrupt-{stamp}");
		}
		catch { /* quarantine is best-effort; not registering still beats bricking */ }
	}

	/// <summary>Resolve the universe layer from a parsed state
	/// file (or a fresh install): every universe's own data, the
	/// wanted set, and the active universe.
	///
	/// MIGRATION (Decision 1C): a state file with no Universes
	/// section predates the multi-universe build. Its flat watched
	/// list, overrides, last-started episode and refresh timestamps
	/// ARE the launch universe's — Chicago was the only universe
	/// those builds knew — and are adopted untouched: nothing
	/// watched, saved, or configured is lost or reset.
	///
	/// MIGRATION (v2.1.0, the upgrade guarantee): a state file
	/// with no InstalledPacks registry predates downloadable
	/// packs — its universes shipped inside the old app, so
	/// nothing is registered for it here. Its wanted universes
	/// (its persisted selection, or for a pre-refactor file the
	/// launch universe) start PENDING: their progress sections sit
	/// dormant in state, untouched, and the packs restore in the
	/// background (PackService.RestorePendingAsync) or from the
	/// pack manager. A fresh install (no state file) wants
	/// nothing and has nothing — setup's pack picker is step one.</summary>
	private void ResolveUniverses(StateFile? state)
	{
		_universes.Clear();
		_dormantSections.Clear();
		bool flatMigrated = false;
		if (state?.Universes != null)
		{
			foreach (var (id, saved) in state.Universes)
			{
				if (saved == null) continue;
				// A section for a universe whose pack is installed
				// goes live; any other is kept dormant, verbatim —
				// progress survives a pack's absence and is adopted
				// back if the pack ever (re)installs.
				if (UniverseCatalogue.Find(id) != null) AdoptSection(id, saved);
				else _dormantSections[id] = saved;
			}
		}
		else if (state != null)
		{
			// Pre-refactor file → its flat per-universe fields are
			// the launch universe's data. Adopted into live data
			// directly: the section must persist across saves
			// while Chicago's pack is still pending its restore.
			flatMigrated = true;
			var data = DataFor(UniverseCatalogue.LegacyUniverseId);
			foreach (var k in state.Watched ?? new List<string>()) data.Watched.Add(k);
			AdoptLinkOverrides(data, null, state.PrimeIds);
			data.LastStartedEpisodeKey = state.LastStartedEpisodeKey ?? "";
			data.LastStartedPositionMs = state.LastStartedPositionMs ?? 0;
			data.LastPositionSavedAtMs = state.LastStartedPositionMs ?? 0;
			data.DataUpdatedAt = ParseTimestamp(state.DataUpdatedAt);
			data.DataRefreshAttemptedAt = ParseTimestamp(state.DataRefreshAttemptedAt);
		}

		_wanted.Clear();
		if (state?.IncludedUniverses != null)
		{
			foreach (var id in state.IncludedUniverses)
				if (!string.IsNullOrWhiteSpace(id) && !_wanted.Contains(id)) _wanted.Add(id);
		}
		else if (state?.Universes != null)
		{
			// A v2 file that never persisted a selection of its
			// own: the universes it holds progress for are the
			// wanted ones.
			foreach (var id in state.Universes.Keys) _wanted.Add(id);
		}
		if (flatMigrated && !_wanted.Contains(UniverseCatalogue.LegacyUniverseId))
			_wanted.Add(UniverseCatalogue.LegacyUniverseId);

		_preferredActiveId = state?.ActiveUniverseId ?? "";
		var live = IncludedUniverseIds;
		ActiveUniverseId = _preferredActiveId.Length > 0 && live.Contains(_preferredActiveId)
			? _preferredActiveId
			: live.Count > 0 ? live[0] : "";
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
			// Universes: nothing wanted, nothing active
			// (ResolveUniverses' fresh-install defaults).
			SetupCompleted = false;
			ResolveUniverses(state: null);
			SaveSettings();
		}
		else
		{
			// The pack registry first (v2.1.0): which packs are
			// installed decides which universes exist at all.
			// Then the universe layer: it adopts the file's
			// per-universe sections — or, for a pre-refactor file,
			// migrates its flat fields into the launch universe
			// (see ResolveUniverses). The active universe's dataset
			// then materializes against that data below.
			RegisterInstalledDownloads(state.InstalledPacks);
			ResolveUniverses(state);
			// Discovered channel ids (Decision 1G): adopted only
			// for known platforms with plausible values; a file
			// predating them simply starts with nothing known.
			_platformChannels.Clear();
			if (state.PlatformChannels != null)
			{
				foreach (var (platform, channelId) in state.PlatformChannels)
				{
					var known = PlatformCatalog.Find(platform);
					if (known != null && !string.IsNullOrWhiteSpace(channelId) && channelId.Length <= 20)
						_platformChannels[known.Signifier] = channelId.Trim();
				}
			}
			// My Services (2026-11-04): adopted only for known
			// signifiers; a file predating the setting leaves the
			// all-three default standing, so nothing changes for
			// existing installs until the user picks.
			if (state.MyServices != null)
			{
				_myServices.Clear();
				foreach (var signifier in state.MyServices)
				{
					var known = PlatformCatalog.Find(signifier);
					if (known != null) _myServices.Add(known.Signifier);
				}
			}
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
			if (ActiveUniverseId.Length > 0)
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
			// Link overrides write in the current per-platform
			// shape; PrimeIds rides along as its prime subset so
			// the previous build, if ever reinstalled, still reads
			// the user's Prime overrides (see UniverseStateFile).
			var primeSubset = new Dictionary<string, string>();
			foreach (var (key, perPlatform) in data.LinkOverrides)
				if (perPlatform.TryGetValue(PlatformCatalog.PrimeSignifier, out var primeId))
					primeSubset[key] = primeId;
			universes[id] = new UniverseStateFile
			{
				Watched = data.Watched.ToList(),
				PrimeIds = primeSubset,
				LinkOverrides = data.LinkOverrides.ToDictionary(
					kv => kv.Key, kv => new Dictionary<string, string>(kv.Value)),
				HiddenCategories = data.HiddenCategories.Count > 0
					? data.HiddenCategories.OrderBy(c => c).ToList()
					: null,
				LastStartedEpisodeKey = data.LastStartedEpisodeKey,
				LastStartedPlatform = data.LastStartedPlatform.Length > 0 ? data.LastStartedPlatform : null,
				LastStartedPositionMs = data.LastStartedPositionMs,
				DataUpdatedAt = FormatTimestamp(data.DataUpdatedAt),
				DataRefreshAttemptedAt = FormatTimestamp(data.DataRefreshAttemptedAt),
			};
		}
		// Dormant sections (universes whose packs aren't
		// installed) round-trip verbatim: saving must never drop
		// a universe's progress just because its pack is absent.
		foreach (var (id, section) in _dormantSections)
			if (!universes.ContainsKey(id)) universes[id] = section;
		var state = new StateFile
		{
			SetupCompleted = SetupCompleted,
			// The WANTED set, verbatim — pending universes stay
			// in the file until they restore or are forgotten,
			// so an offline upgrade never loses its place in
			// line (v2.1.0).
			IncludedUniverses = _wanted.ToList(),
			ActiveUniverseId = ActiveUniverseId,
			Universes = universes,
			InstalledPacks = _downloads.Count > 0
				? _downloads.Select(p => new InstalledPackStateFile
				{
					Id = p.Id,
					DataVersion = p.DataVersion,
					Sha256 = p.Sha256,
					FileName = p.FileName,
					DisplayName = p.DisplayName,
					ShowsLine = p.ShowsLine,
				}).ToList()
				: null,
			PlatformChannels = _platformChannels.Count > 0
				? new Dictionary<string, string>(_platformChannels)
				: null,
			// Always written, even when empty: null means
			// "predates the setting" (all three), an empty list
			// means the user has none of them — the two must
			// never blur. Catalogue order keeps the file stable.
			MyServices = PlatformCatalog.All
				.Select(p => p.Signifier)
				.Where(s => _myServices.Contains(s))
				.ToList(),
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
			// Always written, even when empty: null means
			// "predates the grouping" (TV-only default), an
			// empty list means the user closed every group — the
			// two must never blur (the My Services rule).
			// Page order keeps the file stable.
			SettingsOpenGroups = SettingsGroups
				.Where(g => _openSettingsGroups.Contains(g))
				.ToList(),
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
		_datasetLinks.Clear();
		foreach (var ep in fresh)
		{
			// Serialize has link-normalized the rows; capture the
			// pristine layers exactly as LoadUniverseDatasetAsync
			// does, so the two load paths can never disagree.
			_datasetIds[ep.Key] = ep.PrimeContentId;
			_datasetLinks[ep.Key] = ep.Links
				.Select(l => new EpisodeLink { Platform = l.Platform, Id = l.Id }).ToList();
		}
		data.Episodes = fresh;
		foreach (var ep in fresh) ep.Watched = _watched.Contains(ep.Key);
		ApplyPrimeIds();
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		File.WriteAllText(DataPathFor(ActiveUniverseId), pristineJson);
		// Retire the pre-multi-universe device copy for the launch
		// universe — the per-universe path supersedes it from here.
		if (ActiveUniverseId == UniverseCatalogue.LegacyUniverseId && File.Exists(LegacyDataPath))
			File.Delete(LegacyDataPath);
		SaveSettings();
	}

	/// <summary>the user's hand-saved Prime ID override for an episode
	/// identity, if one exists (used by the data refresh to judge ID
	/// coverage the way the app actually experiences it).</summary>
	public string? GetPrimeOverride(string key) => GetLinkOverride(key, PlatformCatalog.PrimeSignifier);

	/// <summary>The user's hand-saved override for one episode on
	/// one platform, if one exists (Decision 1G).</summary>
	public string? GetLinkOverride(string key, string platformSignifier) =>
		_linkOverrides.TryGetValue(key, out var perPlatform)
		&& perPlatform.TryGetValue(platformSignifier, out var id)
		&& !string.IsNullOrWhiteSpace(id)
			? id
			: null;

	/// <summary>Every hand-saved override in the ACTIVE universe,
	/// as (episode key, platform signifier, ID) — the diagnostics
	/// snapshot's material (it masks the IDs before printing).</summary>
	public IEnumerable<(string Key, string Platform, string Id)> AllLinkOverrides()
	{
		foreach (var (key, perPlatform) in _linkOverrides)
			foreach (var (platform, id) in perPlatform)
				if (!string.IsNullOrWhiteSpace(id))
					yield return (key, platform, id);
	}

	/// <summary>THE resolution of an episode's playable links
	/// (Decision 1G). Layers, in order: the dataset's shipped
	/// links (the pack author's priority order), with the user's
	/// hand-saved override replacing the dataset's link for the
	/// SAME platform in its slot, and an override for a platform
	/// the dataset doesn't carry appended at the end. Returns
	/// fresh copies — callers may not mutate the layers.</summary>
	public List<EpisodeLink> LinksFor(Episode ep) => LinksFor(ActiveData, ep);

	/// <summary>The links Watch can actually use (My Services,
	/// 2026-11-04): LinksFor filtered to the services the user
	/// has, order preserved. Badges and the link editor always
	/// show LinksFor in full; play paths, Watch-button visibility
	/// and autoplay use this. An entry whose links all sit on
	/// services the user doesn't have returns empty here and
	/// behaves as a pure checklist entry.</summary>
	public List<EpisodeLink> WatchLinksFor(Episode ep) =>
		LinksFor(ep).Where(l => _myServices.Contains(l.Platform)).ToList();

	private static List<EpisodeLink> LinksFor(UniverseData data, Episode ep)
	{
		var result = new List<EpisodeLink>();
		IEnumerable<EpisodeLink> datasetLinks;
		if (data.DatasetLinks.TryGetValue(ep.Key, out var shipped))
			datasetLinks = shipped;
		else
			// An episode that never passed through dataset loading
			// (a harness row): its own links, normalized on the fly.
			datasetLinks = ep.Links ?? new List<EpisodeLink>();
		foreach (var link in datasetLinks)
		{
			var platform = PlatformCatalog.Find(link.Platform);
			if (platform == null || string.IsNullOrWhiteSpace(link.Id)) continue;
			if (result.Any(l => l.Platform == platform.Signifier)) continue;
			result.Add(new EpisodeLink { Platform = platform.Signifier, Id = link.Id });
		}
		if (data.LinkOverrides.TryGetValue(ep.Key, out var overrides))
		{
			foreach (var (platformSignifier, id) in overrides)
			{
				var platform = PlatformCatalog.Find(platformSignifier);
				if (platform == null || string.IsNullOrWhiteSpace(id)) continue;
				var existing = result.FirstOrDefault(l => l.Platform == platform.Signifier);
				if (existing != null)
					existing.Id = id;
				else
					result.Add(new EpisodeLink { Platform = platform.Signifier, Id = id });
			}
		}
		return result;
	}

	/// <summary>The platforms an episode could be playing on, for
	/// attributing TV playback to it: the platforms of its
	/// effective links — or, for a tracker-only episode the user may
	/// still start by hand on the TV, Prime only (exactly the
	/// pre-1G attribution). When the app itself last started THIS
	/// episode, its recorded platform outranks the list.</summary>
	public IReadOnlyList<StreamingPlatform> CandidatePlatformsFor(Episode ep)
	{
		if (LastStartedEpisodeKey == ep.Key
			&& PlatformCatalog.Find(LastStartedPlatform) is { } session)
			return new[] { session };
		var platforms = LinksFor(ep)
			.Select(l => PlatformCatalog.Find(l.Platform))
			.Where(p => p != null)
			.Select(p => p!)
			.ToList();
		return platforms.Count > 0 ? platforms : new[] { PlatformCatalog.Prime };
	}

	/// <summary>If the TV's player status belongs to one of the
	/// platforms this episode could be playing on, that platform;
	/// otherwise null. Matching is by plugin id against the
	/// platform's cached channel id (Prime: channel 13), or by
	/// plugin name against the platform's channel name — the
	/// media-player is always the authority the monitor trusts.</summary>
	public StreamingPlatform? MatchedPlatform(MediaPlayerStatus status, Episode ep)
	{
		foreach (var platform in CandidatePlatformsFor(ep))
		{
			var channelId = ChannelIdForPlatform(platform.Signifier);
			if (channelId != null && status.PluginId == channelId) return platform;
			if (status.PluginName != null
				&& string.Equals(status.PluginName, platform.ChannelName, StringComparison.OrdinalIgnoreCase))
				return platform;
		}
		return null;
	}

	/// <summary>Whether the TV's player status is ANY of the
	/// three streaming platforms (by cached channel id or plugin
	/// name) — the state-aware hero button's "the TV has a
	/// streaming app up" test, platform-blind.</summary>
	public bool IsStreamingPlatformStatus(MediaPlayerStatus status)
	{
		foreach (var platform in PlatformCatalog.All)
		{
			var channelId = ChannelIdForPlatform(platform.Signifier);
			if (channelId != null && status.PluginId == channelId) return true;
			if (status.PluginName != null
				&& string.Equals(status.PluginName, platform.ChannelName, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

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
		if (data.LinkOverrides.TryGetValue(ep.Key, out var perPlatform)
			&& perPlatform.TryGetValue(PlatformCatalog.PrimeSignifier, out var own)
			&& !string.IsNullOrWhiteSpace(own)) return own;
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

	/// <summary>Whether an entry is visible under its universe's
	/// category switches (2026-11-04): its effective category is
	/// not switched off. Hidden entries keep their watched state
	/// and every other fact — they simply don't surface.</summary>
	private static bool CategoryVisible(UniverseData data, Episode ep) =>
		!data.HiddenCategories.Contains(ep.EffectiveCategory);

	/// <summary>The ACTIVE universe form of CategoryVisible —
	/// the tracker rows' filter.</summary>
	public bool CategoryVisibleFor(Episode ep) => CategoryVisible(ActiveData, ep);

	/// <summary>The next unwatched VISIBLE entry. Hidden-category
	/// entries are skipped exactly as watched ones are, so Up
	/// Next, the monitor, autoplay and the notification all move
	/// past them without ever touching their progress.</summary>
	public Episode? UpNext => Episodes.FirstOrDefault(e => !e.Watched && CategoryVisible(ActiveData, e));

	public int WatchedCount => Episodes.Count(e => e.Watched);

	public List<Episode> Upcoming(int count)
	{
		var next = UpNext;
		if (next == null) return new();
		return Episodes.Where(e => e.Order > next.Order && !e.Watched && CategoryVisible(ActiveData, e)).Take(count).ToList();
	}

	// ---- Content categories (2026-11-04) ------------------------
	// Per-universe switches over the categories a pack mixes in
	// (films beside episodes, one-shots, web extras…). Stored in
	// the universe's own state section; hiding is
	// non-destructive — the same precedent as universe
	// exclusion — and a universe whose pack holds fewer than two
	// categories shows no switches at all.

	/// <summary>The categories present in a universe's dataset,
	/// with their entry counts, in first-appearance (watch)
	/// order. Loads the dataset on demand, like
	/// UniverseEpisodeCountAsync.</summary>
	public async Task<List<(string Category, int Count)>> UniverseCategoriesAsync(string universeId)
	{
		var episodes = await LoadUniverseDatasetAsync(universeId);
		var counts = new Dictionary<string, int>();
		var order = new List<string>();
		foreach (var ep in episodes)
		{
			var category = ep.EffectiveCategory;
			if (!counts.ContainsKey(category)) order.Add(category);
			counts[category] = counts.GetValueOrDefault(category) + 1;
		}
		return order.Select(c => (c, counts[c])).ToList();
	}

	/// <summary>Whether a category is switched off in a
	/// universe.</summary>
	public bool CategoryHidden(string universeId, string category) =>
		DataFor(universeId).HiddenCategories.Contains(category);

	/// <summary>Switch a category off or on in a universe.
	/// Persists like any other setting; nothing else about the
	/// universe changes.</summary>
	public void SetCategoryHidden(string universeId, string category, bool hidden)
	{
		if (!EpisodeCategories.IsKnown(category)) return;
		var data = DataFor(universeId);
		if (hidden) data.HiddenCategories.Add(category.Trim().ToLowerInvariant());
		else data.HiddenCategories.Remove(category.Trim().ToLowerInvariant());
		SaveSettings();
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
	/// <summary>Include or exclude a universe — one state, two
	/// surfaces: setup's Universes step and the Settings Universes
	/// section both call this. Including takes effect immediately
	/// (the switcher and the rows pick it up from the persisted
	/// set). Excluding never deletes the universe's progress: its
	/// section stays in the state file, and re-including restores
	/// everything exactly where it was left. Excluding the ACTIVE
	/// universe moves the active pointer to another included
	/// universe — or to none, when it was the last one: since
	/// v2.1.0 an install can legitimately have zero live
	/// universes (a fresh install starts there), so exclusion is
	/// never refused for emptying the set. Returns false — and
	/// changes nothing — only for a universe that isn't
	/// installed.</summary>
	public async Task<bool> SetUniverseIncludedAsync(string universeId, bool included)
	{
		if (UniverseCatalogue.Find(universeId) == null) return false;
		if (included)
		{
			if (!_wanted.Contains(universeId))
			{
				_wanted.Add(universeId);
				SaveSettings();
			}
			return true;
		}
		if (!_wanted.Contains(universeId)) return true;
		_wanted.Remove(universeId);
		if (ActiveUniverseId == universeId)
		{
			var live = IncludedUniverseIds;
			if (live.Count > 0)
				await SetActiveUniverseAsync(live[0]);
			else
			{
				ActiveUniverseId = "";
				SaveSettings();
				Changed?.Invoke();
			}
		}
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
		if (!IncludedUniverseIds.Contains(universeId)) return false;
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

	// ---- Downloaded packs (Decision 1J, v2.1.0) -----------------

	/// <summary>Install a pack whose bytes have ALREADY passed
	/// the SHA-256 gate (PackService.InstallFromBytesAsync — the
	/// network boundary verified them against the index). Here
	/// the content faces the full gauntlet every dataset faces:
	/// a self-describing envelope (bare arrays and envelopes
	/// without a display name are refused — a pack must describe
	/// itself), a universe stamp matching the listing it came
	/// under, and DataRefreshService.ValidateDataset over every
	/// episode. Any failure installs NOTHING — the previous
	/// install of the same pack, if there is one, stays exactly
	/// as it was, because the file is only written after every
	/// check has passed.
	///
	/// A successful install: writes the pack file, registers the
	/// universe from its envelope, records the registry entry,
	/// adopts any dormant progress section untouched (a removed
	/// pack's progress, or an upgrade's pending universe, comes
	/// back exactly where it was left), joins the wanted set, and
	/// claims the active pointer when there is none — or when
	/// this pack is the one the state file remembers preferring.
	/// On an UPDATE the refreshed device copy is dropped: it was
	/// merged against the old pack data and must not shadow the
	/// new (the refresh rebuilds it from the new pack).</summary>
	public async Task<PackInstallResult> InstallPackAsync(PackIndexEntry entry, byte[] bytes)
	{
		if (entry == null || !PackIndexParse.IsValidPackId(entry.Id))
			return PackInstallResult.Fail("That universe's listing is damaged — nothing was installed.");
		string json;
		try { json = System.Text.Encoding.UTF8.GetString(bytes); }
		catch { return PackInstallResult.Fail("That universe file couldn't be read — nothing was installed."); }
		var envelope = DatasetFile.ParseEnvelope(json);
		if (envelope == null || envelope.WasBareArray)
			return PackInstallResult.Fail("That universe file isn't a valid pack — nothing was installed.");
		if (!string.Equals(envelope.Universe, entry.Id, StringComparison.Ordinal))
			return PackInstallResult.Fail("That file belongs to a different universe than its listing says — nothing was installed.");
		if (string.IsNullOrWhiteSpace(envelope.DisplayName))
			return PackInstallResult.Fail("That pack doesn't describe itself, so it can't be added — nothing was installed.");
		if (!DataRefreshService.ValidateDataset(envelope.Episodes))
			return PackInstallResult.Fail("That pack's episode data didn't pass the app's checks — nothing was installed.");

		var id = entry.Id;
		bool wasUpdate = _downloads.Any(p => p.Id == id);
		Directory.CreateDirectory(FileSystem.AppDataDirectory);
		await File.WriteAllBytesAsync(PackPathFor(id), bytes);
		var info = UniverseCatalogue.InfoFromEnvelope(envelope, PackFileNameFor(id), entry.ShowsLine);
		UniverseCatalogue.Register(info);
		_downloads.RemoveAll(p => p.Id == id);
		_downloads.Add(new InstalledPackRecord
		{
			Id = id,
			DataVersion = entry.DataVersion >= 1 ? entry.DataVersion : 1,
			Sha256 = entry.Sha256?.Trim().ToLowerInvariant() ?? "",
			FileName = PackFileNameFor(id),
			DisplayName = info.DisplayName,
			ShowsLine = info.ShowsLine,
		});
		if (_dormantSections.Remove(id, out var dormant))
			AdoptSection(id, dormant);
		var data = DataFor(id);
		data.Episodes = null; // re-read from the new pack file on next access
		if (wasUpdate)
		{
			var refreshed = DataPathFor(id);
			if (File.Exists(refreshed)) File.Delete(refreshed);
		}
		if (!_wanted.Contains(id)) _wanted.Add(id);
		if (ActiveUniverseId.Length == 0 || _preferredActiveId == id)
			ActiveUniverseId = id;
		// The active universe's dataset materializes now (its
		// cache was invalidated above — an update of the pack on
		// screen must show its new data, not the old or nothing);
		// any other pack loads on activation, as always.
		if (ActiveUniverseId == id)
			await LoadUniverseDatasetAsync(id);
		SaveSettings();
		return PackInstallResult.Success(wasUpdate);
	}

	/// <summary>Remove an installed pack: its dataset files are
	/// deleted and its universe unregisters. Its PROGRESS IS
	/// KEPT — the non-destructive precedent: the universe's data
	/// stays in the state file (live for this session, dormant
	/// after a restart), and re-downloading the pack brings
	/// everything back exactly where it was left. Removing the
	/// active universe moves the pointer to another included
	/// universe, or to none.</summary>
	public async Task<PackInstallResult> RemovePackAsync(string universeId)
	{
		if (FindInstalledPack(universeId) == null || UniverseCatalogue.Find(universeId) == null)
			return PackInstallResult.Fail("That universe isn't installed.");
		try { var p = PackPathFor(universeId); if (File.Exists(p)) File.Delete(p); } catch { /* the unregister below still stands */ }
		try { var r = DataPathFor(universeId); if (File.Exists(r)) File.Delete(r); } catch { /* a derivative copy; best effort */ }
		if (universeId == UniverseCatalogue.LegacyUniverseId)
		{
			try { if (File.Exists(LegacyDataPath)) File.Delete(LegacyDataPath); } catch { /* pre-refactor copy; best effort */ }
		}
		UniverseCatalogue.Unregister(universeId);
		_downloads.RemoveAll(p => p.Id == universeId);
		_wanted.Remove(universeId);
		if (ActiveUniverseId == universeId)
		{
			var live = IncludedUniverseIds;
			if (live.Count > 0)
				await SetActiveUniverseAsync(live[0]);
			else
			{
				ActiveUniverseId = "";
				SaveSettings();
				Changed?.Invoke();
			}
		}
		else
		{
			SaveSettings();
			Changed?.Invoke();
		}
		return PackInstallResult.Done("Removed. Your progress in it is kept — download it again and everything is where you left it.");
	}

	/// <summary>Forget a pending universe (one an upgraded
	/// install previously had included, whose pack never
	/// restored — typically because it is no longer offered):
	/// it leaves the wanted set, so it stops pending. Its
	/// progress section, if any, stays in state untouched —
	/// forgetting stops the restore, it doesn't erase history;
	/// downloading the pack again later still adopts it back.</summary>
	public bool ForgetPendingUniverse(string universeId)
	{
		if (UniverseCatalogue.Find(universeId) != null) return false;
		if (!_wanted.Remove(universeId)) return false;
		if (_preferredActiveId == universeId) _preferredActiveId = "";
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
	public bool SetPrimeContentId(Episode ep, string id) =>
		SetLinkOverride(ep, PlatformCatalog.PrimeSignifier, id);

	/// <summary>Save a hand-entered link override for one
	/// platform (Decision 1G — SetPrimeContentId generalized).
	/// The platform's own ID rules extract the bare ID from a
	/// pasted link (PlatformIdRules); a value carrying no valid
	/// ID for that platform is REJECTED — nothing is stored and
	/// the caller gets false. An empty value clears that
	/// platform's override and returns true.</summary>
	public bool SetLinkOverride(Episode ep, string platformSignifier, string raw)
	{
		var platform = PlatformCatalog.Find(platformSignifier);
		if (platform == null) return false;
		if (string.IsNullOrWhiteSpace(raw))
		{
			// Clearing an override falls back to the dataset's
			// shipped link for that platform, if it has one.
			if (_linkOverrides.TryGetValue(ep.Key, out var existing))
			{
				existing.Remove(platform.Signifier);
				if (existing.Count == 0) _linkOverrides.Remove(ep.Key);
			}
			if (platform.IsPrime) ep.PrimeContentId = PrimeIdFor(ep);
			SaveSettings();
			return true;
		}
		var normalized = PlatformIdRules.Normalize(platform.Signifier, raw);
		if (!PlatformIdRules.IsValid(platform.Signifier, normalized)) return false;
		if (!_linkOverrides.TryGetValue(ep.Key, out var perPlatform))
			_linkOverrides[ep.Key] = perPlatform = new Dictionary<string, string>();
		perPlatform[platform.Signifier] = normalized;
		if (platform.IsPrime) ep.PrimeContentId = PrimeIdFor(ep);
		SaveSettings();
		return true;
	}

	/// <summary>Record that the guided Play sequence got this episode
	/// playing on the TV. Persisted, so the already-playing guard in
	/// GuidedPlayer survives app restarts. The platform it was
	/// started on is recorded too (Decision 1G): playback is only
	/// attributed to the episode on a platform that could be
	/// playing it.</summary>
	public void NoteEpisodeStarted(Episode ep, long positionMs, string platformSignifier = "")
	{
		LastStartedEpisodeKey = ep.Key;
		LastStartedPlatform = PlatformCatalog.Find(platformSignifier)?.Signifier ?? "";
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

	/// <summary>Expand or collapse one Settings page group
	/// (v2.3.0). Unknown ids are ignored; a call that changes
	/// nothing doesn't rewrite the state file.</summary>
	public void SetSettingsGroupOpen(string groupId, bool open)
	{
		if (!SettingsGroups.Contains(groupId)) return;
		bool changed = open
			? _openSettingsGroups.Add(groupId)
			: _openSettingsGroups.Remove(groupId);
		if (changed) SaveSettings();
	}

	public void SetCreditsThreshold(string mode)
	{
		CreditsThreshold = mode is CreditsBalanced or CreditsLetPlay ? mode : CreditsBalanced;
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
	/// prompt has been shown, whatever the user chose — it never nags
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
			: UniverseCatalogue.LegacyUniverseId;
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
			if (s.TryGetProperty("creditsThreshold", out var credits) && credits.ValueKind == JsonValueKind.String)
				CreditsThreshold = credits.GetString() is CreditsBalanced or CreditsLetPlay
					? credits.GetString()!
					: CreditsBalanced;
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
		if (ActiveUniverseId != UniverseCatalogue.LegacyUniverseId) return ImportResult.WrongUniverse;

		var validKeys = Episodes.Select(e => e.Key).ToHashSet();
		_watched.Clear();
		foreach (var k in state.Watched)
			if (validKeys.Contains(k)) _watched.Add(k);
		// Legacy backups carry Prime-only overrides; they land in
		// the per-platform store as prime overrides (Decision 1G).
		_linkOverrides.Clear();
		if (state.PrimeIds != null)
			foreach (var kv in state.PrimeIds)
				if (validKeys.Contains(kv.Key) && IsValidContentId(kv.Value))
					_linkOverrides[kv.Key] = new Dictionary<string, string>
					{
						[PlatformCatalog.PrimeSignifier] = kv.Value,
					};
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
	/// <summary>"Start over": wipe the state file and reproduce a
	/// first install exactly — NOTHING watched (Decision 7: no
	/// seed, for anyone, ever), every setting back at its default,
	/// and setup offered again on the next appearance. Since
	/// v2.1.0 a first install also has NO universes: every
	/// installed pack file and refreshed copy is deleted, the
	/// catalogue empties, and the pack picker in setup is where
	/// universes come from again.</summary>
	public async Task ClearAllAsync()
	{
		if (File.Exists(StatePath)) File.Delete(StatePath);
		if (File.Exists(LegacyDataPath)) File.Delete(LegacyDataPath);
		// Every pack's files go: the registered ones (catalogue)
		// and any the registry still names.
		var packIds = UniverseCatalogue.All.Select(u => u.Id)
			.Concat(_downloads.Select(d => d.Id)).Distinct().ToList();
		foreach (var id in packIds)
		{
			var refreshed = DataPathFor(id);
			if (File.Exists(refreshed)) File.Delete(refreshed);
			var pack = PackPathFor(id);
			if (File.Exists(pack)) File.Delete(pack);
		}
		foreach (var id in packIds) UniverseCatalogue.Unregister(id);
		_downloads.Clear();
		PackService.ClearIndexCache();

		// Every universe's progress goes with the state file:
		// fresh UniverseData all round, nothing wanted, nothing
		// dormant, nothing active — the fresh-install state.
		_universes.Clear();
		_dormantSections.Clear();
		_wanted.Clear();
		_preferredActiveId = "";
		ActiveUniverseId = "";
		_platformChannels.Clear();
		// My Services returns to its fresh-install state too:
		// all three selected.
		_myServices.Clear();
		foreach (var p in PlatformCatalog.All) _myServices.Add(p.Signifier);

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
		// Settings groups back to the first-install default:
		// TV expanded, everything else collapsed.
		_openSettingsGroups.Clear();
		_openSettingsGroups.Add(SettingsGroupTv);
		// A first install has not done setup — start-over returns to
		// exactly that state (Decision 7).
		SetupCompleted = false;

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
