namespace OneChicago.Services;

/// <summary>
/// One universe (pack) known to the app: an interwoven set of
/// shows with its own watch order, its own episode dataset, and
/// its own shipped episode links. A universe is DATA, not app
/// identity (Decision 1C) — every feature in the app works off
/// whichever universe is active, and nothing outside this
/// catalogue and the dataset files knows any universe's
/// specifics.
///
/// Since v2.1.0 (Decision 1J + Sam's rulings of 2026-11-04) a
/// UniverseInfo is NEVER written in code: every pack declares
/// this metadata in its own dataset envelope (see DatasetFile)
/// and is registered from that declaration — the packs that once
/// shipped inside the app (One Chicago, CSI) exactly like packs
/// downloaded later. The app ships with NO universes at all; a
/// fresh install downloads the ones its user picks.
/// </summary>
public sealed class UniverseInfo
{
	/// <summary>Stable identity, stamped into the universe's
	/// dataset files (see DatasetFile), its state-file section,
	/// and its backups. Never renamed once shipped.</summary>
	public required string Id { get; init; }

	/// <summary>The name shown in setup's Universes step, the
	/// Settings Universes section, and the tracker switcher.
	/// Declared by the pack's envelope.</summary>
	public required string DisplayName { get; init; }

	/// <summary>One line naming the universe's shows, shown under
	/// its name wherever universes are listed. Declared by the
	/// pack's envelope.</summary>
	public required string ShowsLine { get; init; }

	/// <summary>Name of the pack's dataset file in app data
	/// (Store.PackFileNameFor) — the file the pack was installed
	/// from. There is no embedded dataset asset any more.</summary>
	public required string DatasetFileName { get; init; }

	/// <summary>The TVmaze show-id map for this universe's data
	/// refresh (dataset show name → TVmaze show id), or null when
	/// the universe has NO refresh mapping: its dataset then stays
	/// exactly as installed, forever — the refresh never runs for
	/// it (see DataRefreshService). Declared by the pack's
	/// envelope (a show mapped to null there is simply absent
	/// here).</summary>
	public IReadOnlyDictionary<string, int>? TvMazeShowIds { get; init; }

	/// <summary>Whether the refresh ever runs for this universe.</summary>
	public bool HasRefreshMapping => TvMazeShowIds != null;
}

/// <summary>
/// The catalogue of universes this install of the app knows —
/// the union of every pack installed on the device, registered
/// from the packs' own self-describing envelopes by the Store
/// (at startup from the installed-pack registry, and at install
/// time when a pack downloads). Empty on a fresh install, by
/// design (Sam's ruling, 2026-11-04): setup's first step is
/// choosing which packs to download.
///
/// The registry list itself is internal (not private) for
/// exactly one reason: the verification harness compiles these
/// sources directly into its own assembly and resets/rebuilds
/// the registry per scenario. The shipped app only ever adds to
/// it through the Store's registration paths.
/// </summary>
public static class UniverseCatalogue
{
	/// <summary>The launch universe's id — kept as a constant
	/// because HISTORY is stamped with it: pre-multi-universe
	/// state files and backups belong to this universe (Chicago
	/// was the only one those builds knew), and its pre-refactor
	/// refreshed-dataset file is adopted under this id. It is a
	/// migration key, not a registration: this universe registers
	/// from its pack envelope like every other, and only when its
	/// pack is installed.</summary>
	public const string LegacyUniverseId = "one-chicago";

	internal static readonly List<UniverseInfo> Registry = new();

	/// <summary>Every known universe, in registration order
	/// (which is display order wherever universes are listed).</summary>
	public static IReadOnlyList<UniverseInfo> All => Registry;

	/// <summary>Look a universe up by id; null when this install
	/// doesn't have it (a state file can outlive a pack —
	/// unknown ids are ignored, never fatal).</summary>
	public static UniverseInfo? Find(string? id) =>
		id == null ? null : Registry.FirstOrDefault(u => u.Id == id);

	/// <summary>Build a catalogue entry from a pack's own
	/// self-describing envelope — THE registration path, the only
	/// one there is. The TVmaze map converts to the refresh's
	/// shape (shows the envelope maps to null drop out; no map at
	/// all stays no map). A missing display name falls back to the
	/// universe id: registration must never fail on cosmetics
	/// (the pack INSTALLER is stricter — it refuses a pack whose
	/// envelope declares no display name).</summary>
	public static UniverseInfo InfoFromEnvelope(DatasetFile envelope, string datasetFileName, string? fallbackShowsLine = null)
	{
		Dictionary<string, int>? tvMaze = null;
		if (envelope.TvMazeShowIds != null)
		{
			tvMaze = new Dictionary<string, int>();
			foreach (var (show, sid) in envelope.TvMazeShowIds)
				if (sid is int id) tvMaze[show] = id;
		}
		return new UniverseInfo
		{
			Id = envelope.Universe,
			DisplayName = !string.IsNullOrWhiteSpace(envelope.DisplayName)
				? envelope.DisplayName.Trim()
				: envelope.Universe,
			ShowsLine = !string.IsNullOrWhiteSpace(envelope.ShowsLine)
				? envelope.ShowsLine.Trim()
				: fallbackShowsLine ?? "",
			DatasetFileName = datasetFileName,
			TvMazeShowIds = tvMaze,
		};
	}

	/// <summary>Register a universe (or replace the entry with
	/// the same id — a pack update re-registers from its new
	/// envelope, keeping its position in the list). Returns false
	/// only for a blank id.</summary>
	public static bool Register(UniverseInfo info)
	{
		if (info == null || string.IsNullOrWhiteSpace(info.Id)) return false;
		int existing = Registry.FindIndex(u => u.Id == info.Id);
		if (existing >= 0) Registry[existing] = info;
		else Registry.Add(info);
		return true;
	}

	/// <summary>Forget a universe (its pack was removed). The
	/// caller's state handling decides what happens to its
	/// progress — the Store keeps it, dormant, for a re-download.</summary>
	public static bool Unregister(string id) =>
		Registry.RemoveAll(u => u.Id == id) > 0;

	/// <summary>Harness only: clear the registry so a scenario
	/// can rebuild it from its own state directory. The shipped
	/// app never calls this — its catalogue is built once, at
	/// startup, from the packs actually installed.</summary>
	internal static void ResetRegistry() => Registry.Clear();
}
