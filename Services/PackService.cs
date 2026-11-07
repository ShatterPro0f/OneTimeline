using System.Net.Http.Headers;

namespace OneChicago.Services;

/// <summary>
/// The network half of downloadable packs (Decision 1J): fetch
/// the pack index from the app's GitHub repository, download a
/// pack's dataset bytes, verify them against the index's
/// SHA-256, and hand them to the Store to install. Everything
/// fetched goes through BoundedRead with a hard cap and the
/// client's timeout; every failure mode returns a plain result
/// or null — this service never throws at its callers.
///
/// The Store owns everything after the bytes arrive (parsing,
/// validation, registration, state). The two seams are separate
/// on purpose: InstallFromBytesAsync is the whole verify+install
/// path with the network removed, which is what the setup picker
/// uses per pack and what the verification harness drives.
/// </summary>
public static class PackService
{
	/// <summary>Where the pack index lives: the repository's
	/// packs folder, read raw. Datasets sit beside it.</summary>
	public const string IndexUrl = "https://raw.githubusercontent.com/ShatterPro0f/OneTimeline/main/packs/index.json";
	public const string PacksBaseUrl = "https://raw.githubusercontent.com/ShatterPro0f/OneTimeline/main/packs/";

	private static readonly HttpClient Http = CreateHttp();

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
		// An honest user agent, same as the data refresh's.
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OneTimeline", "2.1"));
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(universe pack downloads)"));
		return http;
	}

	/// <summary>The last-good index cache: a successful fetch is
	/// kept on the device so the pack lists still render offline
	/// (marked as cached, so nobody mistakes it for fresh).</summary>
	public static string IndexCachePath =>
		Path.Combine(FileSystem.AppDataDirectory, "pack-index-cache.json");

	/// <summary>Forget the cached index (Start over calls this:
	/// a first install has no cache).</summary>
	public static void ClearIndexCache()
	{
		try { if (File.Exists(IndexCachePath)) File.Delete(IndexCachePath); }
		catch { /* best effort — a stale cache is a display aid only */ }
	}

	/// <summary>The cached index, parsed, or null when there is
	/// no usable cache.</summary>
	public static List<PackIndexEntry>? LoadCachedIndex()
	{
		try
		{
			if (!File.Exists(IndexCachePath)) return null;
			if (new FileInfo(IndexCachePath).Length > BoundedRead.PackIndexCapBytes) return null;
			return PackIndexParse.Parse(File.ReadAllText(IndexCachePath));
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Fetch the pack index: the network first (a good
	/// fetch refreshes the cache), the last-good cache when the
	/// fetch fails, and (null, false) when neither works.</summary>
	public static async Task<(List<PackIndexEntry>? Entries, bool FromCache)> FetchIndexAsync()
	{
		try
		{
			var json = await BoundedRead.GetStringAsync(Http, IndexUrl, BoundedRead.PackIndexCapBytes);
			var entries = PackIndexParse.Parse(json);
			if (entries != null)
			{
				try
				{
					Directory.CreateDirectory(FileSystem.AppDataDirectory);
					await File.WriteAllTextAsync(IndexCachePath, json);
				}
				catch { /* cache write is best effort */ }
				return (entries, false);
			}
		}
		catch
		{
			// Fall through to the cache: offline is a normal state,
			// not an error worth surfacing when a cache exists.
		}
		var cached = LoadCachedIndex();
		return cached != null ? (cached, true) : (null, false);
	}

	/// <summary>Download one pack's dataset bytes (bounded by the
	/// dataset cap). Null on any failure — offline, a missing
	/// file, an oversized response.</summary>
	public static async Task<byte[]?> DownloadBytesAsync(PackIndexEntry entry)
	{
		try
		{
			return await BoundedRead.GetBytesAsync(Http, PacksBaseUrl + entry.File, BoundedRead.RefreshCapBytes);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Verify + install already-downloaded bytes: the
	/// SHA-256 gate runs HERE, over the exact bytes, before the
	/// Store decodes or parses anything. A mismatch installs
	/// nothing and says so plainly.</summary>
	public static async Task<PackInstallResult> InstallFromBytesAsync(Store store, PackIndexEntry entry, byte[] bytes)
	{
		if (bytes == null || bytes.Length == 0)
			return PackInstallResult.Fail("The download came back empty — nothing was installed.");
		if (bytes.Length > BoundedRead.RefreshCapBytes)
			return PackInstallResult.Fail("That universe file is too large to install safely — nothing was installed.");
		if (!PackIndexParse.VerifySha256(bytes, entry.Sha256))
			return PackInstallResult.Fail("The downloaded file doesn't match its fingerprint in the list, so it was thrown away. Nothing was installed.");
		return await store.InstallPackAsync(entry, bytes);
	}

	/// <summary>Download one pack from the index and install it.</summary>
	public static async Task<PackInstallResult> DownloadAndInstallAsync(Store store, PackIndexEntry entry)
	{
		var bytes = await DownloadBytesAsync(entry);
		if (bytes == null)
			return PackInstallResult.Fail("Couldn't download that universe — check your connection and try again.");
		return await InstallFromBytesAsync(store, entry, bytes);
	}

	/// <summary>
	/// Restore the universes an upgraded install previously had
	/// included but whose packs aren't on the device (the
	/// upgrade guarantee, 2026-11-04): fetch each one's dataset
	/// and install it. Progress lives in the state file and is
	/// never touched by this — an install simply makes a dormant
	/// universe live again, exactly where the user left it.
	///
	/// Best-effort and silent: a pack that can't be fetched (the
	/// device is offline, the index has no entry for it) STAYS
	/// pending, and the pack manager shows it as "needs download"
	/// with a retry button. Returns how many packs were restored.
	/// </summary>
	public static async Task<int> RestorePendingAsync(Store store)
	{
		var (entries, _) = await FetchIndexAsync();
		if (entries == null) return 0;
		return await RestorePendingAsync(store, entries, DownloadBytesAsync);
	}

	/// <summary>The restore, with its sources injected (the
	/// harness drives this form with local files).</summary>
	public static async Task<int> RestorePendingAsync(
		Store store,
		IReadOnlyList<PackIndexEntry> entries,
		Func<PackIndexEntry, Task<byte[]?>> fetchBytes)
	{
		int restored = 0;
		foreach (var id in store.PendingRestoreIds.ToList())
		{
			var entry = entries.FirstOrDefault(e => e.Id == id);
			if (entry == null) continue; // not offered any more; stays pending for the pack manager's Forget path
			try
			{
				var bytes = await fetchBytes(entry);
				if (bytes == null) continue;
				var result = await InstallFromBytesAsync(store, entry, bytes);
				if (result.Ok) restored++;
			}
			catch
			{
				// One pack's failure never blocks the others, and
				// never surfaces: pending is a visible, retryable
				// state, not an error.
			}
		}
		return restored;
	}

	/// <summary>Best-effort download size for a pack (a HEAD
	/// request's Content-Length), for the picker's "· 1.2 MB"
	/// line. Null when the server doesn't say — the size is a
	/// courtesy, never worth failing a render over.</summary>
	public static async Task<long?> ProbeSizeAsync(PackIndexEntry entry)
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			using var req = new HttpRequestMessage(HttpMethod.Head, PacksBaseUrl + entry.File);
			using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
			if (!resp.IsSuccessStatusCode) return null;
			return resp.Content.Headers.ContentLength;
		}
		catch
		{
			return null;
		}
	}
}
