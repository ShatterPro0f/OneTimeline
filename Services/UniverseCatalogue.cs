namespace OneChicago.Services;

/// <summary>
/// One universe (pack) known to this build of the app: an
/// interwoven set of shows with its own watch order, its own
/// episode dataset, and its own shipped episode links. A universe
/// is DATA, not app identity (Decision 1C) — every feature in the
/// app works off whichever universe is active, and nothing outside
/// this catalogue and the dataset files knows any universe's
/// specifics.
/// </summary>
public sealed class UniverseInfo
{
	/// <summary>Stable identity, stamped into the universe's
	/// dataset files (see DatasetFile), its state-file section,
	/// and its backups. Never renamed once shipped.</summary>
	public required string Id { get; init; }

	/// <summary>The name shown in setup's Universes step, the
	/// Settings Universes section, and the tracker switcher.</summary>
	public required string DisplayName { get; init; }

	/// <summary>One line naming the universe's shows, shown under
	/// its name wherever universes are listed.</summary>
	public required string ShowsLine { get; init; }

	/// <summary>Logical name of the embedded dataset asset
	/// (Resources/Raw) that seeds this universe on a fresh
	/// install.</summary>
	public required string DatasetResourceName { get; init; }

	/// <summary>The TVmaze show-id map for this universe's data
	/// refresh (dataset show name → TVmaze show id), or null when
	/// the universe has NO refresh mapping: its dataset then stays
	/// exactly as shipped, forever — the refresh never runs for
	/// it (see DataRefreshService).</summary>
	public IReadOnlyDictionary<string, int>? TvMazeShowIds { get; init; }

	/// <summary>Whether the refresh ever runs for this universe.</summary>
	public bool HasRefreshMapping => TvMazeShowIds != null;
}

/// <summary>
/// The catalogue of universes this build of the app knows
/// (Decision 1C). One Chicago is the first entry; CSI is the
/// second (Decision 1E). Adding a universe to the app means adding an entry here
/// plus its dataset file; the selection UI (setup + Settings),
/// per-universe progress, the active-universe plumbing, backup
/// scoping and diagnostics all pick it up from this list.
///
/// The registry list itself is internal (not private) for exactly
/// one reason: the verification harness compiles these sources
/// directly into its own assembly and registers a synthetic pack
/// to prove per-universe isolation. Nothing in the shipped app
/// adds to it at runtime.
/// </summary>
public static class UniverseCatalogue
{
	/// <summary>The launch universe: the Chicago shows and their
	/// crossover guests, in the community spreadsheet's order
	/// (credited in Settings → About), with its episode links
	/// shipped in the dataset (Decision 1 as amended).</summary>
	public static readonly UniverseInfo OneChicago = new()
	{
		Id = "one-chicago",
		DisplayName = "One Chicago",
		ShowsLine = "Chicago Fire · Chicago P.D. · Chicago Med · Chicago Justice — plus FBI & Law & Order: SVU crossovers",
		DatasetResourceName = "episodes.json",
		TvMazeShowIds = new Dictionary<string, int>
		{
			["Fire"] = 59,
			["PD"] = 56,
			["Med"] = 2246,
			["Justice"] = 12327,
			["FBI"] = 32158,
			["Law & Order: SVU"] = 103,
		},
	};

	/// <summary>The second universe: the CSI shows in the
	/// community spreadsheet "CSI Universe Release Order"'s
	/// release order, plus the single Without a Trace episode
	/// that completes the "Who &amp; What" crossover (Decision
	/// 1E). Its shipped episode links are Paramount+ Amazon
	/// Channel content IDs — they open inside Prime Video with
	/// the Paramount+ channel add-on; CSI: Cyber and a few other
	/// episodes have no Canadian streaming link and are
	/// tracker-only. Full provenance: docs/CSI-SOURCES.md in the
	/// project repository.</summary>
	public static readonly UniverseInfo Csi = new()
	{
		Id = "csi",
		DisplayName = "CSI",
		ShowsLine = "CSI: Crime Scene Investigation · CSI: Miami · CSI: NY · CSI: Cyber · CSI: Vegas — plus a Without a Trace crossover",
		DatasetResourceName = "episodes-csi.json",
		TvMazeShowIds = new Dictionary<string, int>
		{
			["CSI"] = 109,
			["Miami"] = 576,
			["NY"] = 749,
			["Cyber"] = 735,
			["Vegas"] = 50361,
			["WAT"] = 813,
		},
	};

	internal static readonly List<UniverseInfo> Registry = new() { OneChicago, Csi };

	/// <summary>Every known universe, in display order.</summary>
	public static IReadOnlyList<UniverseInfo> All => Registry;

	/// <summary>The default universe: the first catalogue entry.
	/// A fresh install includes every catalogue universe and starts
	/// on this one; a migrated install's progress belongs to it
	/// (it was the only universe pre-refactor builds knew).</summary>
	public static UniverseInfo Default => Registry[0];

	/// <summary>The default universe's id ("one-chicago").</summary>
	public static string DefaultId => Registry[0].Id;

	/// <summary>Look a universe up by id; null when this build
	/// doesn't know it (a state file can outlive the catalogue —
	/// unknown ids are ignored, never fatal).</summary>
	public static UniverseInfo? Find(string? id) =>
		id == null ? null : Registry.FirstOrDefault(u => u.Id == id);
}
