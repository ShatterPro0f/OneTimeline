using System.Globalization;
using System.Text.Json.Serialization;

namespace OneChicago.Models;

/// <summary>
/// One episode in the watch order. The shipped dataset carries facts
/// only (production Decision 1): watch order, show / season / episode
/// numbers, code, title, air date, cameo appearances, crossover and
/// multi-part flags, and runtime — plus the episode description,
/// which comes from TVmaze and is credited in the app (CC BY-SA).
/// The watch-order sheet's written notes are NOT shipped (someone
/// else's expression); the factual event flags derived from them
/// are. Prime Video content IDs DO ship for the launch universe
/// (Decision 1 as amended 2026-11-03); a user's own links — a
/// fetched episode-links source, or a hand-saved per-episode
/// override — live outside the dataset and outrank it (Store).
/// </summary>
public class Episode
{
	[JsonPropertyName("order")] public int Order { get; set; }
	[JsonPropertyName("key")] public string Key { get; set; } = "";
	[JsonPropertyName("sheetNumber")] public int? SheetNumber { get; set; }
	[JsonPropertyName("show")] public string Show { get; set; } = "";
	[JsonPropertyName("season")] public int Season { get; set; }
	[JsonPropertyName("episodeNumber")] public int EpisodeNumber { get; set; }
	[JsonPropertyName("code")] public string Code { get; set; } = "";
	[JsonPropertyName("title")] public string Title { get; set; } = "";
	[JsonPropertyName("airDate")] public string? AirDate { get; set; }
	[JsonPropertyName("description")] public string? Description { get; set; }
	[JsonPropertyName("runtimeMinutes")] public int? RuntimeMinutes { get; set; }
	[JsonPropertyName("cameos")] public List<Cameo> Cameos { get; set; } = new();
	[JsonPropertyName("eventKind")] public string? EventKind { get; set; }
	[JsonPropertyName("eventIndex")] public int? EventIndex { get; set; }
	[JsonPropertyName("eventTotal")] public int? EventTotal { get; set; }
	[JsonPropertyName("eventGroup")] public string? EventGroup { get; set; }

	/// <summary>The Prime Video content ID used to play this episode.
	/// The launch universe's dataset ships its episodes' links in
	/// this field (Decision 1 as amended 2026-11-03). At runtime the
	/// Store keeps it resolved to the effective ID — the dataset's
	/// link, a user-fetched source link, or the user's hand-saved
	/// override, in that precedence order (Store.PrimeIdFor). Empty
	/// means tracker-only.</summary>
	[JsonPropertyName("primeContentId")] public string PrimeContentId { get; set; } = "";

	// Local state, not part of the shipped dataset.
	[JsonIgnore] public bool Watched { get; set; }

	[JsonIgnore] public bool IsEvent => EventKind != null;

	/// <summary>Full show name for display ("Chicago Med", not "Med").</summary>
	[JsonIgnore]
	public string DisplayShow => Show switch
	{
		"Fire" => "Chicago Fire",
		"PD" => "Chicago P.D.",
		"Med" => "Chicago Med",
		"Justice" => "Chicago Justice",
		_ => Show,
	};

	[JsonIgnore] public string SeasonEpisode => $"S{Season} E{EpisodeNumber}";

	/// <summary>Air date in friendly form ("Nov 17, 2015"); falls back to
	/// the raw dataset value when it doesn't parse.</summary>
	[JsonIgnore]
	public string AirDateDisplay => DateTime.TryParse(AirDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
		? d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
		: (AirDate ?? "");

	/// <summary>Short event pill text for the clean design, e.g.
	/// "Crossover · Part 1 of 3". Empty for ordinary episodes.</summary>
	[JsonIgnore]
	public string EventPill => EventKind switch
	{
		"crossover" => $"Crossover · Part {EventIndex} of {EventTotal}",
		"part" => $"Multi-part · Part {EventIndex} of {EventTotal}",
		_ => "",
	};

	[JsonIgnore]
	public string EventLabel => EventKind switch
	{
		"crossover" => $"🚨 CROSSOVER EVENT — PART {EventIndex} OF {EventTotal}",
		"part" => $"🧩 MULTI-PART STORY — PART {EventIndex} OF {EventTotal}",
		_ => "",
	};

	[JsonIgnore] public string Headline => $"{Show} {Code} — “{Title}”";

	[JsonIgnore]
	public string MetaLine
	{
		get
		{
			var bits = new List<string> { $"#{Order}" };
			if (!string.IsNullOrEmpty(AirDate)) bits.Add(AirDate);
			if (RuntimeMinutes is int r) bits.Add($"{r} min");
			if (Cameos.Count > 0) bits.Add($"Cameos: {string.Join(", ", Cameos.Select(c => c.Show))}");
			return string.Join(" · ", bits);
		}
	}

	[JsonIgnore] public Color ShowColor => Show switch
	{
		"Fire" => Color.FromArgb("#E64B35"),
		"PD" => Color.FromArgb("#3B7DD8"),
		"Med" => Color.FromArgb("#2AA198"),
		"Justice" => Color.FromArgb("#8E6CC8"),
		"FBI" => Color.FromArgb("#C9A227"),
		"Law & Order: SVU" => Color.FromArgb("#7A8699"),
		_ => Color.FromArgb("#999999"),
	};

	[JsonIgnore] public string WatchedGlyph => Watched ? "☑" : "☐";
}

public class Cameo
{
	[JsonPropertyName("show")] public string Show { get; set; } = "";
	[JsonPropertyName("names")] public string Names { get; set; } = "";
}
