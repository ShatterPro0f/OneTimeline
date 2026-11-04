using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// Automatic data refresh — production form (Decision 1).
///
/// The watch order SHIPS IN THE APP. It is a table of facts (which
/// episode follows which, titles, numbers, air dates, crossover
/// flags, runtimes) compiled into the embedded dataset, and nothing
/// about it is fetched at runtime: the production build never calls
/// the third-party spreadsheet the personal build scraped, and it
/// never calls JustWatch at all (no Prime IDs are fetched here —
/// the launch universe's links ship in the dataset itself, and any
/// further links are the user's own hand-saved per-episode
/// overrides).
///
/// What a refresh DOES fetch, on every cold start, without blocking
/// the UI: episode metadata from TVmaze — descriptions, plus an
/// air-date fallback for any episode the shipped data lacks a date
/// for. TVmaze data is CC BY-SA and is credited in the app
/// (Settings → About, and a source line wherever a description is
/// shown). TVmaze's per-episode "runtime" field is deliberately NOT
/// used: it is the broadcast slot length (typically 60 minutes),
/// not the episode's actual length, and the completion detection
/// judges episode endings against runtimes — the shipped factual
/// runtimes stay authoritative.
///
/// The refresh starts from the currently loaded dataset and produces
/// a copy with TVmaze's metadata merged in by episode identity. The
/// swap happens only if the merged dataset passes the full
/// validation (ValidateDataset — the same checks a previously
/// refreshed dataset faces on load). Any failure — offline, an
/// endpoint changing shape, a validation miss — keeps the existing
/// dataset and is INVISIBLE: no error UI, no toast. The only surface
/// is the "Data updated · &lt;date&gt;" line in Settings.
///
/// Concurrency: single-flight — repeated opens while a run is in
/// flight collapse into that run; a fresh run still starts on the
/// next cold start. The swap itself (Store.ReplaceDataset) re-keys
/// everything by episode identity, so the monitor, an in-flight
/// guided sequence or an autoplay countdown are never disturbed:
/// they hold their own episode reference / identity, and Up Next
/// simply re-resolves against the new list.
/// </summary>
public class DataRefreshService
{
	private readonly Store _store;
	private int _running; // 0 = idle, 1 = a refresh is in flight

	public DataRefreshService(Store store) => _store = store;

	private static readonly HttpClient Http = CreateHttp();

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
		// An honest user agent (audit 1.2: the old build impersonated
		// a browser for the unofficial endpoint it no longer calls).
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneTimeline", "1.9"));
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(episode data refresh; TVmaze API)"));
		return http;
	}

	/// <summary>Fire-and-forget entry point (AppHost calls this once
	/// per cold start, after the store has loaded). Never throws.</summary>
	public async Task RefreshOnStartAsync()
	{
		// Per-universe (Decision 1C): the refresh runs only for a
		// universe whose catalogue entry declares a TVmaze mapping.
		// A universe without one keeps its shipped dataset forever —
		// the refresh genuinely never runs for it, and no attempt is
		// recorded against it. (Chicago, the only mapped universe
		// today, runs exactly the shipped behaviour below.)
		if (_store.ActiveUniverse.TvMazeShowIds == null) return;
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
		try
		{
			_store.MarkDataRefreshAttempted(DateTimeOffset.Now);
			var fresh = await BuildDatasetAsync();
			if (fresh != null && Validate(fresh))
			{
				if (Store.DatasetFingerprint(fresh) != Store.DatasetFingerprint(_store.Episodes))
					_store.ReplaceDataset(fresh);
				_store.MarkDataRefreshed(DateTimeOffset.Now);
			}
		}
		catch
		{
			// Silent by design: the stored dataset stays, untouched.
		}
		finally
		{
			Volatile.Write(ref _running, 0);
		}
	}

	private bool Validate(List<Episode> fresh) => ValidateDataset(fresh, _store.Episodes);

	// Per-field length caps (audit 1.4). Generous against the shipped
	// dataset (longest description ~900 chars, longest title 52)
	// while still catching injected absurdity.
	private const int MaxTitleLength = 1000;
	private const int MaxDescriptionLength = 2000;
	private const int MaxCameoTextLength = 500;
	private const int MaxShowLength = 100;
	private const int MaxCodeLength = 50;

	/// <summary>
	/// The full production validation for a dataset (audit 1.4),
	/// applied identically to a fresh refresh AND to a previously
	/// refreshed dataset loaded from disk. Absolute checks: minimum
	/// count; every episode has show/code/title within the field
	/// caps; descriptions and cameos capped; episode identity (key
	/// AND show|season|episode) and order are unique; season /
	/// episode / order numbers and runtimes sit in plausible ranges
	/// (shipped runtimes span 39–120 min; 5–600 allowed); event
	/// indices sane. Any Prime ID present in a dataset row must be a
	/// strict GTI (the launch universe ships its links in the
	/// dataset; the user's own further IDs live outside it, as
	/// hand-saved per-episode overrides). Relative check
	/// (when a current dataset is supplied): count within a sane
	/// band. Any failure means "keep the last known-good dataset."
	/// </summary>
	public static bool ValidateDataset(List<Episode> fresh, IReadOnlyList<Episode>? current = null)
	{
		if (fresh == null || fresh.Count < 100) return false;
		if (current is { Count: > 0 })
		{
			if (fresh.Count < current.Count * 0.9) return false;
			if (fresh.Count > current.Count + 100) return false;
		}
		var keys = new HashSet<string>();
		var identities = new HashSet<string>();
		var orders = new HashSet<int>();
		foreach (var ep in fresh)
		{
			if (string.IsNullOrWhiteSpace(ep.Show) || ep.Show.Length > MaxShowLength
				|| string.IsNullOrWhiteSpace(ep.Code) || ep.Code.Length > MaxCodeLength
				|| string.IsNullOrWhiteSpace(ep.Title) || ep.Title.Length > MaxTitleLength) return false;
			if (ep.Description is { Length: > MaxDescriptionLength }) return false;
			if (ep.Cameos is { Count: > 20 }) return false;
			foreach (var cameo in ep.Cameos ?? new List<Cameo>())
			{
				if ((cameo.Show?.Length ?? 0) > MaxShowLength || (cameo.Names?.Length ?? 0) > MaxCameoTextLength) return false;
			}
			if (!string.IsNullOrEmpty(ep.PrimeContentId) && !Store.IsValidContentId(ep.PrimeContentId)) return false;
			// The multi-platform fields (Decision 1G) face the same
			// strictness: a known entry kind, and links that are few,
			// on known platforms, formally valid for their platform,
			// and at most one per platform.
			if (ep.Kind != null && ep.Kind != "episode" && ep.Kind != "movie") return false;
			if (ep.Category != null && !EpisodeCategories.IsKnown(ep.Category)) return false;
			if (ep.Links is { Count: > 8 }) return false;
			if (ep.Links != null)
			{
				var seenPlatforms = new HashSet<string>();
				foreach (var link in ep.Links)
				{
					var platform = PlatformCatalog.Find(link?.Platform);
					if (platform == null || link == null) return false;
					if (!PlatformIdRules.IsValid(platform.Signifier, link.Id)) return false;
					if (!seenPlatforms.Add(platform.Signifier)) return false;
				}
			}
			if (string.IsNullOrWhiteSpace(ep.Key) || !keys.Add(ep.Key)) return false;
			if (!identities.Add($"{ep.Show}|{ep.Season}|{ep.EpisodeNumber}")) return false;
			if (!orders.Add(ep.Order)) return false;
			if (ep.Season is < 1 or > 100) return false;
			if (ep.EpisodeNumber is < 1 or > 999) return false;
			if (ep.Order is < 1 or > 100_000) return false;
			if (ep.SheetNumber is < 0 or > 100_000) return false;
			if (ep.RuntimeMinutes is not (>= 5 and <= 600)) return false;
			if (ep.EventIndex is int ei && (ei < 1 || ei > 20)) return false;
			if (ep.EventTotal is int et && (et < 1 || et > 20)) return false;
			if (ep.EventIndex is int ei2 && ep.EventTotal is int et2 && ei2 > et2) return false;
		}
		return true;
	}

	// ---- Pipeline ---------------------------------------------------

	/// <summary>Clone the loaded dataset (the shipped watch order and
	/// its facts, exactly as they stand) with TVmaze metadata merged
	/// in by episode identity. Each row's shipped Prime ID is carried
	/// through untouched (the launch universe's dataset ships its
	/// links — Decision 1 as amended); the refresh contributes
	/// TVmaze metadata only, never an ID of its own, and
	/// Store.ReplaceDataset reapplies the user's hand-saved
	/// overrides on top afterwards.</summary>
	private async Task<List<Episode>?> BuildDatasetAsync()
	{
		var tvmaze = await FetchTvmazeAsync();
		if (tvmaze.Count == 0) return null;

		var rows = new List<Episode>(_store.Episodes.Count);
		foreach (var ep in _store.Episodes)
		{
			tvmaze.TryGetValue(ep.Key, out var tv);
			rows.Add(new Episode
			{
				Order = ep.Order,
				Key = ep.Key,
				SheetNumber = ep.SheetNumber,
				Show = ep.Show,
				Season = ep.Season,
				EpisodeNumber = ep.EpisodeNumber,
				Code = ep.Code,
				Title = ep.Title,
				AirDate = ep.AirDate ?? tv?.AirDate,
				Description = tv?.Description ?? ep.Description,
				RuntimeMinutes = ep.RuntimeMinutes,
				Cameos = new List<Cameo>(ep.Cameos),
				EventKind = ep.EventKind,
				EventIndex = ep.EventIndex,
				EventTotal = ep.EventTotal,
				EventGroup = ep.EventGroup,
				// The SHIPPED id, via the Store's pristine layer —
				// ep.PrimeContentId at this moment is the effective
				// ID, which may be the user's own link; baking that
				// into a dataset row would corrupt the layer order.
				PrimeContentId = _store.GetDatasetContentId(ep.Key),
				// The multi-platform fields ride through untouched
				// (Decision 1G): the refresh contributes TVmaze
				// metadata only — it never invents or drops the
				// entry kind or its streaming links. The copies are
				// pristine; overrides live in their own Store layer.
				Kind = ep.Kind,
				Category = ep.Category,
				Links = (ep.Links ?? new List<EpisodeLink>()).Select(l => new EpisodeLink { Platform = l.Platform, Id = l.Id }).ToList(),
			});
		}
		return rows;
	}

	// ---- Source: TVmaze (descriptions; air-date fallback) -----------

	// The TVmaze show-id map lives on the universe's catalogue
	// entry (UniverseCatalogue, Decision 1C) — a universe declares
	// its own mapping, or none. Chicago's ids are unchanged.

	private class TvInfo
	{
		public string? Description;
		public string? AirDate;
	}

	/// <summary>Every refresh fetch goes through the bounded reader
	/// (audit 1.2): a hostile or malfunctioning source can no longer
	/// hand the app an unbounded response to hold in memory.</summary>
	private async Task<string> GetStringAsync(string url) =>
		await BoundedRead.GetStringAsync(Http, url, BoundedRead.RefreshCapBytes);

	private static string StripHtml(string html)
	{
		var s = Regex.Replace(html, "<[^>]*>", " ");
		foreach (var (a, b) in new[]
		{
			("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""),
			("&#039;", "'"), ("&#39;", "'"), ("&rsquo;", "’"), ("&lsquo;", "‘"),
			("&rdquo;", "”"), ("&ldquo;", "“"), ("&hellip;", "…"), ("&nbsp;", " "),
		})
			s = s.Replace(a, b);
		return Regex.Replace(s, @"\s+", " ").Trim();
	}

	private async Task<Dictionary<string, TvInfo>> FetchTvmazeAsync()
	{
		var output = new Dictionary<string, TvInfo>();
		// The ACTIVE universe's declared mapping; RefreshOnStartAsync
		// has already gated on it being present.
		var showIds = _store.ActiveUniverse.TvMazeShowIds;
		if (showIds == null) return output;
		foreach (var (show, sid) in showIds)
		{
			using var epsDoc = JsonDocument.Parse(await GetStringAsync($"https://api.tvmaze.com/shows/{sid}/episodes"));
			foreach (var ep in epsDoc.RootElement.EnumerateArray())
			{
				if (!ep.TryGetProperty("season", out var seasonEl) || seasonEl.ValueKind != JsonValueKind.Number) continue;
				if (!ep.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number) continue;
				var summary = ep.TryGetProperty("summary", out var sumEl) && sumEl.ValueKind == JsonValueKind.String
					? StripHtml(sumEl.GetString() ?? "")
					: "";
				output[$"{show}|{seasonEl.GetInt32()}|{numberEl.GetInt32()}"] = new TvInfo
				{
					Description = summary.Length > 0 ? summary : null,
					AirDate = ep.TryGetProperty("airdate", out var adEl) && adEl.ValueKind == JsonValueKind.String
						? adEl.GetString() : null,
				};
			}
		}
		return output;
	}
}
