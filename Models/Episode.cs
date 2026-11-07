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
/// (Decision 1 as amended 2026-11-03); a user's own hand-saved
/// per-episode overrides live outside the dataset and outrank it
/// (Store).
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
	/// Store keeps it resolved to the effective ID — the user's
	/// hand-saved override when one exists, else the dataset's link
	/// (Store.PrimeIdFor). Empty means tracker-only.
	///
	/// Multi-platform (Decision 1G): this field is the LEGACY form
	/// of a Prime link. New datasets express every playable ID in
	/// <see cref="Links"/> with a platform signifier; at load, a
	/// primeContentId with no links is normalized into a prime
	/// link, so both shapes behave identically everywhere.</summary>
	[JsonPropertyName("primeContentId")] public string PrimeContentId { get; set; } = "";

	/// <summary>What kind of entry this is (Decision 1G): absent /
	/// "episode" for an ordinary series episode; "movie" for a
	/// film, Special Presentation, One-Shot or any other standalone
	/// piece. The kind decides the deep-link mediaType the player
	/// sends (see DeepLinkMediaType) — streaming services route
	/// movies and episodes differently.</summary>
	[JsonPropertyName("kind")] public string? Kind { get; set; }

	/// <summary>The entry's content category (the project ruling,
	/// 2026-11-04) — what KIND of content it is for the
	/// per-universe category switches. Distinct from
	/// <see cref="Kind"/>, which only drives the deep-link
	/// mediaType. An explicit value in the dataset wins
	/// ("special", "one-shot", "web" — the known values live in
	/// EpisodeCategories); without one, the category is derived:
	/// "film" for a movie entry, "episode" for everything else
	/// (see EffectiveCategory). A user can hide a whole category
	/// in a universe — its entries leave the tracker and Up Next
	/// without losing any progress.</summary>
	[JsonPropertyName("category")] public string? Category { get; set; }

	/// <summary>The effective category: the explicit value,
	/// normalized, or the derived one.</summary>
	[JsonIgnore] public string EffectiveCategory =>
		!string.IsNullOrWhiteSpace(Category)
			? Category.Trim().ToLowerInvariant()
			: IsMovieEntry ? "film" : "episode";

	/// <summary>This entry's playable links, as shipped in the
	/// dataset (Decision 1G): one entry per streaming platform
	/// that carries it, in the pack author's priority order. The
	/// Store resolves the EFFECTIVE links (these, with the user's
	/// per-platform overrides applied) via Store.LinksFor — this
	/// list itself always stays the dataset's own layer.</summary>
	[JsonPropertyName("links")] public List<EpisodeLink> Links { get; set; } = new();

	/// <summary>True for a standalone entry (film / special /
	/// one-shot): its deep links go out with mediaType "movie".</summary>
	[JsonIgnore] public bool IsMovieEntry => Kind == "movie";

	/// <summary>The mediaType this entry's deep links carry:
	/// "movie" for standalone entries, "episode" otherwise.</summary>
	[JsonIgnore] public string DeepLinkMediaType => IsMovieEntry ? "movie" : "episode";

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
		"CSI" => "CSI: Crime Scene Investigation",
		"Miami" => "CSI: Miami",
		"NY" => "CSI: NY",
		"Cyber" => "CSI: Cyber",
		"Vegas" => "CSI: Vegas",
		"WAT" => "Without a Trace",
		_ => Show,
	};

	/// <summary>The per-entry indicator (Decision 1K): ONLY a
	/// show episode renders a season/episode label. Every other
	/// category renders a plain kind label, so a film, one-shot,
	/// web entry or special never displays an S{n}E{n} label
	/// (datasets carry season/episode numbers on those entries
	/// only as ordering data, and the old unconditional label
	/// showed e.g. "S1 E1" on movies and one-shots).</summary>
	[JsonIgnore]
	public string SeasonEpisode => EffectiveCategory switch
	{
		"film" => "Movie",
		"one-shot" => "One-Shot",
		"web" => "Web Series",
		"special" => "Special",
		_ => $"S{Season} E{EpisodeNumber}",
	};

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
		"CSI" => Color.FromArgb("#B08D3E"),
		"Miami" => Color.FromArgb("#E8875A"),
		"NY" => Color.FromArgb("#5B7DA3"),
		"Cyber" => Color.FromArgb("#4FA3A5"),
		"Vegas" => Color.FromArgb("#C44B6E"),
		"WAT" => Color.FromArgb("#8A94A6"),
		_ => Color.FromArgb("#999999"),
	};

	[JsonIgnore] public string WatchedGlyph => Watched ? "☑" : "☐";
}

public class Cameo
{
	[JsonPropertyName("show")] public string Show { get; set; } = "";
	[JsonPropertyName("names")] public string Names { get; set; } = "";
}

/// <summary>
/// One playable link for an episode (Decision 1G): a streaming
/// platform signifier ("prime" / "netflix" / "disney") and that
/// platform's own content ID for this episode, in the platform's
/// native form — a Prime GTI, a Netflix episode watch-ID, a
/// Disney+ entity UUID. The platform table (Services.Platforms)
/// knows each signifier's channel name, ID rules and readback
/// behaviour; nothing else in the app hardcodes a platform.
/// </summary>
public class EpisodeLink
{
	[JsonPropertyName("platform")] public string Platform { get; set; } = "";
	[JsonPropertyName("id")] public string Id { get; set; } = "";
}
