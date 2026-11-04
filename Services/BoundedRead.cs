using System.Text;

namespace OneChicago.Services;

/// <summary>
/// Bounded response/file reads (audit 1.2/1.5): every byte stream
/// that arrives from outside the app — data-refresh responses, Roku
/// device-info answers, the Settings import file — is read with a
/// hard byte cap instead of being read whole into memory. A cap
/// trip throws InvalidDataException; every caller treats that as
/// "this operation failed" and keeps whatever data it already had.
/// </summary>
public static class BoundedRead
{
	/// <summary>Roku ECP answers are small XML documents; 64 KB is
	/// generous headroom.</summary>
	public const int RokuCapBytes = 64 * 1024;

	/// <summary>Refresh responses: the whole shipped dataset is
	/// ~0.7 MB of JSON, and the largest single source response (a
	/// TVmaze episode list) is of the same order; 10 MB is generous
	/// headroom above the real data.</summary>
	public const int RefreshCapBytes = 10 * 1024 * 1024;

	/// <summary>Import files: a real progress backup is a few
	/// hundred KB at most; 2 MB is the audit's suggested cap.</summary>
	public const int ImportCapBytes = 2 * 1024 * 1024;

	/// <summary>GET a URL and read the body as a string, failing if
	/// it exceeds <paramref name="maxBytes"/>.</summary>
	public static async Task<string> GetStringAsync(HttpClient http, string url, int maxBytes, CancellationToken ct = default)
	{
		using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
		resp.EnsureSuccessStatusCode();
		return await ReadContentStringAsync(resp.Content, maxBytes, ct);
	}

	/// <summary>Read an HttpContent body as a string with a hard
	/// byte cap (checks Content-Length up front when present, then
	/// counts while streaming).</summary>
	public static async Task<string> ReadContentStringAsync(HttpContent content, int maxBytes, CancellationToken ct = default)
	{
		if (content.Headers.ContentLength is long len && len > maxBytes)
			throw new InvalidDataException($"Response is {len} bytes, over the {maxBytes}-byte cap.");
		using var stream = await content.ReadAsStreamAsync(ct);
		return await ReadStreamStringAsync(stream, maxBytes, ct);
	}

	/// <summary>Read a stream to its end as a UTF-8 string, failing
	/// the moment more than <paramref name="maxBytes"/> have arrived.</summary>
	public static async Task<string> ReadStreamStringAsync(Stream stream, int maxBytes, CancellationToken ct = default)
	{
		using var ms = new MemoryStream();
		var buffer = new byte[16 * 1024];
		int read;
		while ((read = await stream.ReadAsync(buffer, ct)) > 0)
		{
			if (ms.Length + read > maxBytes)
				throw new InvalidDataException($"Content exceeds the {maxBytes}-byte cap.");
			ms.Write(buffer, 0, read);
		}
		return Encoding.UTF8.GetString(ms.ToArray());
	}
}
