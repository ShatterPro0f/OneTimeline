using System.Text.Json;
using System.Text.Json.Serialization;
using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// The on-disk dataset format: an ENVELOPE, not a bare episode
/// array —
///
///   { "universe": "one-chicago", "version": 1, "episodes": [ … ] }
///
/// The universe stamp exists for forward compatibility: this app is
/// expected to grow beyond a single interwoven universe (the Chicago
/// shows are the first; others can be added as further packs), and a
/// dataset file that names its universe lets a second universe's
/// dataset sit alongside in exactly the same shape, told apart by
/// the stamp instead of by fragile conventions. The single-universe
/// assumption lives ONLY here, at the persistence boundary: every
/// consumer inside the app works on the plain episode list.
///
/// Reading is deliberately two-way tolerant:
///  - an envelope whose universe matches the expected one → its
///    episodes;
///  - an envelope naming a DIFFERENT universe → null (another
///    universe's data must never load into this tracker);
///  - a legacy bare array (device copies written before the
///    envelope existed) → the episodes, treated as this universe.
/// Writing always produces the envelope.
/// </summary>
public class DatasetFile
{
	[JsonPropertyName("universe")] public string Universe { get; set; } = "";
	[JsonPropertyName("version")] public int Version { get; set; } = 1;

	/// <summary>The universe's display name, declared by the pack
	/// itself (Decision 1J + Sam's unification ruling, 2026-11-04:
	/// EVERY pack — the ones that once shipped inside the app and
	/// downloaded ones alike — carries the same self-describing
	/// envelope, and the app registers universes from these fields,
	/// never from code). Optional in the format so embedded-era
	/// files still parse; a pack offered for download must carry
	/// it (the installer rejects one that doesn't).</summary>
	[JsonPropertyName("displayName")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? DisplayName { get; set; }

	/// <summary>One line naming the pack's shows, also declared by
	/// the pack itself; shown wherever universes are listed.</summary>
	[JsonPropertyName("showsLine")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ShowsLine { get; set; }

	/// <summary>The pack's TVmaze show-id map for the metadata
	/// refresh (dataset show name → TVmaze show id). A null VALUE
	/// names a show the pack deliberately leaves unmapped; a null
	/// MAP means the pack has no refresh at all.</summary>
	[JsonPropertyName("tvMazeShowIds")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, int?>? TvMazeShowIds { get; set; }

	[JsonPropertyName("episodes")] public List<Episode> Episodes { get; set; } = new();

	/// <summary>True when the parsed text was a legacy bare episode
	/// array rather than an envelope (set by ParseEnvelope; never
	/// serialized).</summary>
	[JsonIgnore] public bool WasBareArray { get; private set; }

	/// <summary>Parse dataset JSON into its full envelope —
	/// metadata and episodes together — or null if the text isn't
	/// a usable dataset at all. A legacy bare array parses as an
	/// envelope with no metadata (Universe ""). Episode rows are
	/// link-normalized exactly as in <see cref="Parse"/>.</summary>
	public static DatasetFile? ParseEnvelope(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			DatasetFile? file;
			var trimmed = json.TrimStart();
			if (trimmed.StartsWith("["))
			{
				// Legacy bare array (pre-envelope device copy).
				var episodes = JsonSerializer.Deserialize<List<Episode>>(json);
				if (episodes == null) return null;
				file = new DatasetFile { Universe = "", Episodes = episodes, WasBareArray = true };
			}
			else
			{
				file = JsonSerializer.Deserialize<DatasetFile>(json);
				if (file == null) return null;
			}
			// Multi-platform normalization (Decision 1G): a legacy
			// primeContentId becomes a prime link here, at the
			// boundary, so every consumer downstream sees one
			// uniform links shape whatever form the file used.
			foreach (var ep in file.Episodes) LinkNormalization.Apply(ep);
			return file;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Parse dataset JSON (an installed pack file or a
	/// refreshed device copy) into its episode list, or null if
	/// the text isn't a usable dataset for
	/// <paramref name="expectedUniverse"/>.</summary>
	public static List<Episode>? Parse(string json, string expectedUniverse)
	{
		var file = ParseEnvelope(json);
		if (file == null) return null;
		// A bare array carries no universe stamp: it is treated as
		// this universe, as pre-envelope device copies always were.
		// An envelope must name the expected universe exactly.
		if (!file.WasBareArray
			&& !string.Equals(file.Universe, expectedUniverse, StringComparison.Ordinal))
			return null;
		return file.Episodes;
	}

	/// <summary>Serialize an episode list as an envelope stamped
	/// with its universe — the only dataset form the app writes.
	/// Rows are link-normalized first (Decision 1G), so a written
	/// dataset always carries the synced links + primeContentId
	/// pair, whichever form its rows arrived in.</summary>
	public static string Serialize(IReadOnlyList<Episode> episodes, string universe)
	{
		var rows = episodes as List<Episode> ?? new List<Episode>(episodes);
		foreach (var ep in rows) LinkNormalization.Apply(ep);
		var file = new DatasetFile
		{
			Universe = universe,
			Version = 1,
			Episodes = rows,
		};
		return JsonSerializer.Serialize(file);
	}
}
