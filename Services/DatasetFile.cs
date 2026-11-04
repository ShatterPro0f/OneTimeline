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
	[JsonPropertyName("episodes")] public List<Episode> Episodes { get; set; } = new();

	/// <summary>Parse dataset JSON (embedded seed or a device copy)
	/// into its episode list, or null if the text isn't a usable
	/// dataset for <paramref name="expectedUniverse"/>.</summary>
	public static List<Episode>? Parse(string json, string expectedUniverse)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		try
		{
			List<Episode>? episodes;
			var trimmed = json.TrimStart();
			if (trimmed.StartsWith("["))
			{
				// Legacy bare array (pre-envelope device copy).
				episodes = JsonSerializer.Deserialize<List<Episode>>(json);
			}
			else
			{
				var file = JsonSerializer.Deserialize<DatasetFile>(json);
				if (file == null) return null;
				if (!string.Equals(file.Universe, expectedUniverse, StringComparison.Ordinal)) return null;
				episodes = file.Episodes;
			}
			// Multi-platform normalization (Decision 1G): a legacy
			// primeContentId becomes a prime link here, at the
			// boundary, so every consumer downstream sees one
			// uniform links shape whatever form the file used.
			if (episodes != null)
				foreach (var ep in episodes) LinkNormalization.Apply(ep);
			return episodes;
		}
		catch
		{
			return null;
		}
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
