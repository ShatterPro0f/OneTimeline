using System.Net.Http.Headers;
using System.Text.Json;

namespace OneChicago.Services;

/// <summary>
/// The episode-links file format — the bulk counterpart to the
/// per-episode "Your Prime Video link" editor (Decision 1, extended
/// at the user's direction, 2026-11-03). The app ships NO episode
/// links; a user who has an episode-links file (their own, or one
/// they trust) points the app at its address and the app downloads
/// it once, on an explicit Fetch tap — never silently, never in
/// the background.
///
/// File shape (version 1):
///
///   {
///     "format":   "onetimeline-prime-links",
///     "version":  1,
///     "universe": "one-chicago",
///     "links":    { "Fire|1|1": "amzn1.dv.gti.…", … }
///   }
///
/// Keys are episode identities — the dataset's own Episode.Key
/// ("Show|Season|Episode"). Values are Prime Video share links or
/// bare content IDs; each one faces the exact strict-GTI extraction
/// the per-episode editor uses (Store.NormalizeContentId /
/// Store.IsValidContentId), so a fetched link is held to precisely
/// the same standard as a pasted one.
/// </summary>
public static class SourceLinksDocument
{
	public const string FormatId = "onetimeline-prime-links";
	public const int FormatVersion = 1;

	/// <summary>Parse and validate an episode-links document against
	/// the running dataset. Pure and fully validating: on success,
	/// <paramref name="links"/> holds every usable link (episode key
	/// → strict GTI) and <paramref name="ignored"/> counts the
	/// entries that were skipped (unknown episode key, or a value
	/// with no valid Prime ID in it). On ANY structural failure —
	/// unparseable, wrong format, wrong universe, no links list —
	/// returns false with a specific, user-presentable
	/// <paramref name="error"/> and nothing is applied anywhere;
	/// the caller either swaps in the whole validated set or keeps
	/// what it already had.</summary>
	public static bool TryParse(string json, IReadOnlySet<string> validKeys, string universe,
		out Dictionary<string, string> links, out int ignored, out string error)
	{
		links = new Dictionary<string, string>();
		ignored = 0;
		error = "";

		JsonDocument doc;
		try { doc = JsonDocument.Parse(json); }
		catch
		{
			error = "That file isn't readable as an episode-links file (it isn't valid JSON). Nothing was changed.";
			return false;
		}
		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				error = "That file isn't an episode-links file. Nothing was changed.";
				return false;
			}
			if (!root.TryGetProperty("format", out var format)
				|| format.ValueKind != JsonValueKind.String
				|| format.GetString() != FormatId)
			{
				error = $"That file isn't an episode-links file (it doesn't say \"format\": \"{FormatId}\"). Nothing was changed.";
				return false;
			}
			if (root.TryGetProperty("version", out var version))
			{
				if (version.ValueKind != JsonValueKind.Number
					|| !version.TryGetInt32(out var v) || v != FormatVersion)
				{
					error = "That episode-links file is a newer format than this app understands. Nothing was changed.";
					return false;
				}
			}
			if (!root.TryGetProperty("universe", out var fileUniverse)
				|| fileUniverse.ValueKind != JsonValueKind.String
				|| fileUniverse.GetString() != universe)
			{
				var theirs = fileUniverse.ValueKind == JsonValueKind.String ? fileUniverse.GetString() : null;
				error = theirs != null
					? $"That episode-links file is for a different set of shows (\"{theirs}\") — this app tracks \"{universe}\". Nothing was changed."
					: "That episode-links file doesn't say which set of shows it belongs to. Nothing was changed.";
				return false;
			}
			if (!root.TryGetProperty("links", out var linksEl)
				|| linksEl.ValueKind != JsonValueKind.Object)
			{
				error = "That episode-links file has no \"links\" list. Nothing was changed.";
				return false;
			}
			foreach (var entry in linksEl.EnumerateObject())
			{
				if (entry.Value.ValueKind != JsonValueKind.String)
				{
					ignored++;
					continue;
				}
				var normalized = Store.NormalizeContentId(entry.Value.GetString() ?? "");
				if (!Store.IsValidContentId(normalized) || !validKeys.Contains(entry.Name))
				{
					ignored++;
					continue;
				}
				links[entry.Name] = normalized;
			}
		}
		return true;
	}
}

/// <summary>
/// Fetches the user's episode-links file from the address THEY
/// supplied — an explicit Fetch tap in the Sources section (Setup
/// or Settings), never automatic. The address may be http or https
/// and may be hosted on the user's own network; the download is
/// bounded to the same 2 MB cap progress imports face (audit 1.5)
/// with a 30-second timeout, and carries the app's honest user
/// agent. The whole file is validated before anything is stored
/// (SourceLinksDocument.TryParse); only then does the Store swap
/// the layer in atomically, so a bad file can never half-apply.
/// The fetched layer lives on the device only (Store) and is never
/// part of a progress export or a data refresh.
/// </summary>
public class SourceLinksService
{
	private readonly Store _store;

	public SourceLinksService(Store store) => _store = store;

	private static readonly HttpClient Http = CreateHttp();

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
		// The same honest user agent the data refresh uses.
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneTimeline", "1.9"));
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(episode links fetch; address supplied by the user)"));
		return http;
	}

	public record Result(bool Ok, string Message, int Applied, int Ignored);

	/// <summary>Download, validate and (only on full success) apply
	/// the episode-links file at <paramref name="rawUrl"/>. Every
	/// failure is a specific, honest message and leaves the current
	/// links — loaded or not — exactly as they were.</summary>
	public async Task<Result> FetchAsync(string rawUrl)
	{
		var url = (rawUrl ?? "").Trim();
		if (url.Length == 0)
			return new Result(false, "Paste the address of an episode-links file first.", 0, 0);
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
			return new Result(false, "That doesn't look like a web address — it should start with http:// or https://.", 0, 0);

		string json;
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			json = await BoundedRead.GetStringAsync(Http, url, BoundedRead.ImportCapBytes, cts.Token);
		}
		catch (InvalidDataException)
		{
			return new Result(false, "That file is too large to be an episode-links file (2 MB limit). Nothing was changed.", 0, 0);
		}
		catch (HttpRequestException ex)
		{
			var status = ex.StatusCode is { } code ? $" (HTTP {(int)code})" : "";
			return new Result(false, $"Couldn't download the file{status} — check the address and that the phone can reach it. Nothing was changed.", 0, 0);
		}
		catch (TaskCanceledException)
		{
			return new Result(false, "The download timed out — check the address and the connection, then try again. Nothing was changed.", 0, 0);
		}
		catch (Exception ex)
		{
			return new Result(false, $"The download failed ({ex.Message}). Nothing was changed.", 0, 0);
		}

		var validKeys = _store.Episodes.Select(e => e.Key).ToHashSet();
		if (!SourceLinksDocument.TryParse(json, validKeys, Store.DatasetUniverseId,
			out var links, out var ignored, out var error))
			return new Result(false, error, 0, 0);
		if (links.Count == 0)
			return new Result(false, "That file read fine, but none of its links matched an episode in this app — nothing was loaded.", 0, ignored);

		var fetchedAt = DateTimeOffset.Now;
		_store.ApplySourceLinks(url, links, ignored, fetchedAt);
		var message = $"✓ Loaded: {links.Count} episode links · fetched {fetchedAt.LocalDateTime:MMM d, yyyy}";
		if (ignored > 0)
			message += $" {ignored} {(ignored == 1 ? "entry" : "entries")} in the file didn't match an episode here and {(ignored == 1 ? "was" : "were")} skipped.";
		return new Result(true, message, links.Count, ignored);
	}
}
