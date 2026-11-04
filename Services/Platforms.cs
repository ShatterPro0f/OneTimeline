using System.Text.RegularExpressions;
using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// One streaming platform the app can drive on the TV (Decision
/// 1G). Everything platform-specific lives in this table — the
/// dataset signifier, the display names, the channel's name as it
/// appears in the TV's /query/apps list (how the app finds the
/// channel's ECP id on any given TV), whether the platform's
/// media-player reports a duration, and the shape of its content
/// IDs. Behavioural evidence for every row: platform-test-findings
/// (all three proven on Sam's TV, 2026-11-04).
/// </summary>
public sealed class StreamingPlatform
{
	/// <summary>The dataset signifier: the value a dataset's
	/// links[] entries carry in their "platform" field.</summary>
	public required string Signifier { get; init; }

	/// <summary>Full display name ("Prime Video", "Netflix",
	/// "Disney+").</summary>
	public required string DisplayName { get; init; }

	/// <summary>Short badge name for episode rows ("Prime",
	/// "Netflix", "Disney+").</summary>
	public required string ShortName { get; init; }

	/// <summary>The channel's name in Roku's /query/apps listing
	/// — used to discover its channel id on the user's TV. Note
	/// Disney's channel is listed as "Disney Plus" (no +).</summary>
	public required string ChannelName { get; init; }

	/// <summary>Whether this platform's media-player reports a
	/// duration alongside the position. Disney+ does; Prime and
	/// Netflix don't (their endings are judged against the
	/// dataset's runtimes instead).</summary>
	public required bool ReportsDuration { get; init; }

	/// <summary>One plain sentence for the episode-page link
	/// editor: where the user finds this platform's link.</summary>
	public required string EditorHint { get; init; }

	public bool IsPrime => Signifier == PlatformCatalog.PrimeSignifier;
}

/// <summary>The platform table, keyed by dataset signifier.</summary>
public static class PlatformCatalog
{
	public const string PrimeSignifier = "prime";
	public const string NetflixSignifier = "netflix";
	public const string DisneySignifier = "disney";

	public static readonly StreamingPlatform Prime = new()
	{
		Signifier = PrimeSignifier,
		DisplayName = "Prime Video",
		ShortName = "Prime",
		ChannelName = "Prime Video",
		ReportsDuration = false,
		EditorHint = "Open the episode in the Prime Video app (or primevideo.com), tap Share, then Copy link.",
	};

	public static readonly StreamingPlatform Netflix = new()
	{
		Signifier = NetflixSignifier,
		DisplayName = "Netflix",
		ShortName = "Netflix",
		ChannelName = "Netflix",
		ReportsDuration = false,
		EditorHint = "Open netflix.com in a browser, start the episode, and copy the address — the number after /watch/ is its ID. A show's own ID won't do: it resumes the show instead of opening the episode.",
	};

	public static readonly StreamingPlatform Disney = new()
	{
		Signifier = DisneySignifier,
		DisplayName = "Disney+",
		ShortName = "Disney+",
		ChannelName = "Disney Plus",
		ReportsDuration = true,
		EditorHint = "Open disneyplus.com in a browser, start the episode, and copy the address — the code after /play/ is its ID.",
	};

	public static readonly IReadOnlyList<StreamingPlatform> All = new[] { Prime, Netflix, Disney };

	public static StreamingPlatform? Find(string? signifier) =>
		signifier == null
			? null
			: All.FirstOrDefault(p => string.Equals(p.Signifier, signifier, StringComparison.OrdinalIgnoreCase));

	public static bool IsKnown(string? signifier) => Find(signifier) != null;
}

/// <summary>
/// Per-platform content-ID rules (Decision 1G). Each platform's
/// IDs live in their own namespace, and the wrong form fails
/// silently on the TV, so every ID the app stores — from a
/// dataset or from the user's own pasted link — is normalized
/// and validated here, the one place that knows the forms:
///  - prime: a strict GTI, extracted from a pasted Prime share
///    link when needed (Store.NormalizeContentId — the shipped
///    rule, unchanged).
///  - netflix: the EPISODE's numeric watch-ID, extracted from a
///    netflix.com/watch/&lt;id&gt; address when pasted as one.
///    (A series/title ID also parses — the format is the same —
///    but it RESUMES the show rather than opening the episode;
///    that warning belongs to the docs, not the validator.)
///  - disney: the episode's entity/play UUID. An "entity-"
///    prefix (as in share links) is stripped — the TV rejects
///    the prefixed form; the UUID is extracted from a pasted
///    /browse/entity-…, /play/… or /video/… address.
/// </summary>
public static class PlatformIdRules
{
	private static readonly Regex NetflixUrlId = new(@"/(?:watch|title)/(\d{5,12})", RegexOptions.Compiled);
	private static readonly Regex NetflixBareId = new(@"\A\d{5,12}\z", RegexOptions.Compiled);
	private static readonly Regex Uuid = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
	private static readonly Regex UuidFull = new(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.Compiled);

	/// <summary>Extract the bare platform ID from whatever was
	/// supplied (a bare ID, or a pasted link/address for the
	/// platform). "" when the value carries no valid ID for the
	/// platform — callers treat "" as "reject, store nothing".</summary>
	public static string Normalize(string platformSignifier, string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return "";
		var value = raw.Trim();
		switch (platformSignifier)
		{
			case PlatformCatalog.PrimeSignifier:
				return Store.NormalizeContentId(value);
			case PlatformCatalog.NetflixSignifier:
			{
				var m = NetflixUrlId.Match(value);
				if (m.Success) return m.Groups[1].Value;
				return NetflixBareId.IsMatch(value) ? value : "";
			}
			case PlatformCatalog.DisneySignifier:
			{
				// A bare UUID anywhere (the /play/ and /browse/
				// forms both embed exactly one); the "entity-"
				// prefix needs no special handling because the
				// match starts at the UUID itself.
				var m = Uuid.Match(value);
				return m.Success ? m.Value : "";
			}
			default:
				return "";
		}
	}

	/// <summary>True when the value is already a bare, valid ID
	/// for the platform (no extraction performed).</summary>
	public static bool IsValid(string platformSignifier, string? id)
	{
		if (string.IsNullOrEmpty(id)) return false;
		return platformSignifier switch
		{
			PlatformCatalog.PrimeSignifier => Store.IsValidContentId(id),
			PlatformCatalog.NetflixSignifier => NetflixBareId.IsMatch(id),
			PlatformCatalog.DisneySignifier => UuidFull.IsMatch(id),
			_ => false,
		};
	}
}

/// <summary>
/// Dataset-link normalization (Decision 1G), applied at every
/// dataset boundary (parse, load, refresh swap, serialize):
///  - links with an unknown platform signifier, an empty ID, or
///    a second entry for a platform already present are dropped;
///  - kind is canonicalized: "movie" stays, anything else
///    becomes the episode default (absent);
///  - the LEGACY primeContentId and the links form are synced:
///    a primeContentId with no prime link becomes the prime
///    link (both shipped packs load exactly as before, with no
///    dataset edits), and a prime link with an empty
///    primeContentId fills the field in, so older readers of
///    the row still see the Prime ID.
/// Idempotent: normalizing a normalized episode changes nothing.
/// </summary>
public static class LinkNormalization
{
	public static void Apply(Episode ep)
	{
		ep.Kind = string.Equals(ep.Kind?.Trim(), "movie", StringComparison.OrdinalIgnoreCase) ? "movie" : null;

		// Category canonical form (2026-11-04): trimmed and
		// lowercased, blank meaning absent (the category is then
		// derived — see Episode.EffectiveCategory). Unknown values
		// are kept as-is here; the refresh validation is what
		// rejects them, so a hand-built pack fails loudly there
		// rather than silently losing its categories at load.
		ep.Category = string.IsNullOrWhiteSpace(ep.Category) ? null : ep.Category.Trim().ToLowerInvariant();

		var clean = new List<EpisodeLink>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var link in ep.Links ?? new List<EpisodeLink>())
		{
			if (link == null) continue;
			var platform = PlatformCatalog.Find(link.Platform);
			if (platform == null) continue;
			if (string.IsNullOrWhiteSpace(link.Id)) continue;
			if (!seen.Add(platform.Signifier)) continue;
			clean.Add(new EpisodeLink { Platform = platform.Signifier, Id = link.Id.Trim() });
		}

		var primeLink = clean.FirstOrDefault(l => l.Platform == PlatformCatalog.PrimeSignifier);
		if (primeLink == null && !string.IsNullOrWhiteSpace(ep.PrimeContentId))
		{
			primeLink = new EpisodeLink
			{
				Platform = PlatformCatalog.PrimeSignifier,
				Id = ep.PrimeContentId.Trim(),
			};
			clean.Insert(0, primeLink);
		}
		if (primeLink != null && string.IsNullOrWhiteSpace(ep.PrimeContentId))
			ep.PrimeContentId = primeLink.Id;

		ep.Links = clean;
	}
}

/// <summary>Content categories (2026-11-04): the known values
/// an entry's "category" field may carry, and their display
/// names for the per-universe category switches. "film" and
/// "episode" are also the DERIVED categories (movie entries
/// and series episodes without an explicit field); "special",
/// "one-shot" and "web" only ever come from the explicit
/// field. A dataset may not invent new values — the refresh
/// validation rejects them — so this list is the contract the
/// universe docs describe.</summary>
public static class EpisodeCategories
{
	public const string Film = "film";
	public const string Episode = "episode";
	public const string Special = "special";
	public const string OneShot = "one-shot";
	public const string Web = "web";

	public static readonly IReadOnlyList<string> Known =
		new[] { Film, Episode, Special, OneShot, Web };

	public static bool IsKnown(string? category) =>
		category != null && Known.Contains(category.Trim().ToLowerInvariant());

	public static string DisplayName(string category) => category switch
	{
		Film => "Films",
		Episode => "Episodes",
		Special => "Specials",
		OneShot => "One-Shots",
		Web => "Web extras",
		_ => category,
	};
}

/// <summary>Link choice (Decision 1G): given an episode's
/// effective links (dataset order = the pack author's priority)
/// and the set of platforms whose channel is installed on the
/// TV, the playable link is the FIRST one whose platform is
/// installed. Pure and static so the harness can pin it.
/// Callers pass the My-Services-filtered list
/// (Store.WatchLinksFor), so a chosen link's platform is always
/// one the user has AND one the TV carries.</summary>
public static class PlayLinkChoice
{
	public static EpisodeLink? Choose(IReadOnlyList<EpisodeLink> links, IReadOnlySet<string> installedPlatforms)
	{
		foreach (var link in links)
		{
			var platform = PlatformCatalog.Find(link.Platform);
			if (platform != null && installedPlatforms.Contains(platform.Signifier))
				return link;
		}
		return null;
	}
}

/// <summary>Everything the player needs to start one episode on
/// one platform: which platform, its content ID, the TV channel
/// id to address, and the deep-link mediaType for the entry.</summary>
public sealed record PlayTarget(StreamingPlatform Platform, string ContentId, string ChannelId, string MediaType);

/// <summary>How resolving an episode's playable target ended.</summary>
public enum PlayResolutionKind
{
	/// <summary>A link whose platform is installed was chosen.</summary>
	Ready,
	/// <summary>The episode carries no links at all (tracker-only).</summary>
	NoLinks,
	/// <summary>The episode has links, but every one sits on a
	/// service the user hasn't selected in My Services — for
	/// this user the entry is a pure checklist entry.</summary>
	NoSelectedService,
	/// <summary>The episode has links, but none of their channels
	/// is installed on the TV.</summary>
	NotInstalled,
	/// <summary>The TV couldn't be asked which channels it has
	/// (off / unreachable) and nothing could be chosen from the
	/// cached knowledge either.</summary>
	TvUnreachable,
}

/// <summary>The outcome of GuidedPlayer.ResolveTargetAsync:
/// either a PlayTarget, or the plain reason there is none —
/// the surfaces render the reason, never a silent failure.</summary>
public sealed class PlayResolution
{
	public PlayResolutionKind Kind { get; private init; }
	public PlayTarget? Target { get; private init; }

	/// <summary>Display names of the platforms the episode HAS
	/// links for (NotInstalled: the ones the TV is missing;
	/// NoSelectedService: the ones the user doesn't have).</summary>
	public IReadOnlyList<string> LinkPlatformNames { get; private init; } = Array.Empty<string>();

	public static PlayResolution Ready(PlayTarget target) =>
		new() { Kind = PlayResolutionKind.Ready, Target = target };

	public static PlayResolution NoLinks() => new() { Kind = PlayResolutionKind.NoLinks };

	public static PlayResolution NoSelectedService(IReadOnlyList<string> platformNames) =>
		new() { Kind = PlayResolutionKind.NoSelectedService, LinkPlatformNames = platformNames };

	public static PlayResolution NotInstalled(IReadOnlyList<string> platformNames) =>
		new() { Kind = PlayResolutionKind.NotInstalled, LinkPlatformNames = platformNames };

	public static PlayResolution Unreachable(IReadOnlyList<string> platformNames) =>
		new() { Kind = PlayResolutionKind.TvUnreachable, LinkPlatformNames = platformNames };
}
