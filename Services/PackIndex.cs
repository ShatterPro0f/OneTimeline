using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OneChicago.Services;

/// <summary>
/// The downloadable-pack index (Decision 1J): a small JSON file
/// in the app's GitHub repository listing every universe pack
/// available for download —
///
///   { "packs": [ { "id": …, "displayName": …, "showsLine": …,
///                  "episodeCount": …, "file": …,
///                  "sha256": …, "dataVersion": … } ] }
///
/// The app fetches it (PackService), setup offers its entries as
/// the first-run picker, and Settings' pack manager works off
/// it. The index is REMOTE DATA: it is parsed defensively —
/// wholly malformed JSON yields null (the caller falls back to
/// its cached copy), and an entry that fails the per-entry rules
/// is skipped, never fatal. What an entry promises is always
/// verified at download time: the bytes must hash to its sha256
/// and parse as a valid dataset for its id before anything
/// registers (Store.InstallPackAsync).
/// </summary>
public sealed class PackIndexEntry
{
	[JsonPropertyName("id")] public string Id { get; set; } = "";
	[JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
	[JsonPropertyName("showsLine")] public string ShowsLine { get; set; } = "";
	[JsonPropertyName("episodeCount")] public int EpisodeCount { get; set; }
	[JsonPropertyName("file")] public string File { get; set; } = "";
	[JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
	[JsonPropertyName("dataVersion")] public int DataVersion { get; set; } = 1;
}

/// <summary>The index document itself (envelope form only).
/// Packs is nullable on purpose: a document WITHOUT a packs
/// array is malformed (null), which must stay distinguishable
/// from a valid index that happens to list no packs ([]).</summary>
public sealed class PackIndex
{
	[JsonPropertyName("packs")] public List<PackIndexEntry>? Packs { get; set; }
}

public static class PackIndexParse
{
	private static readonly Regex IdPattern = new(@"\A[a-z0-9][a-z0-9-]{0,49}\z", RegexOptions.Compiled);
	private static readonly Regex ShaPattern = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.Compiled);

	/// <summary>A pack id is a short lowercase slug — the same
	/// shape the dataset envelope's universe stamp uses. It keys
	/// state sections, file names, and backups, so nothing else
	/// is ever accepted from remote data.</summary>
	public static bool IsValidPackId(string? id) =>
		id != null && IdPattern.IsMatch(id);

	/// <summary>An index entry's file must be a plain file name
	/// inside the packs folder — no directories, no traversal,
	/// always a .json dataset.</summary>
	public static bool IsSafePackFileName(string? file) =>
		!string.IsNullOrWhiteSpace(file)
		&& file.Length <= 100
		&& file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
		&& !file.Contains('/') && !file.Contains('\\')
		&& !file.Contains("..")
		&& file == Path.GetFileName(file);

	/// <summary>Parse index JSON into its valid entries, or null
	/// when the text isn't an index at all. Entries are validated
	/// one by one (id shape, safe file name, a real SHA-256) and
	/// skipped when they fail; display fields degrade gracefully
	/// (name falls back to the id, counts floor at zero, version
	/// floors at 1). Duplicate ids keep the first entry.</summary>
	public static List<PackIndexEntry>? Parse(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;
		PackIndex? index;
		try
		{
			index = JsonSerializer.Deserialize<PackIndex>(json);
		}
		catch
		{
			return null;
		}
		if (index?.Packs == null) return null;
		var entries = new List<PackIndexEntry>();
		var seen = new HashSet<string>();
		foreach (var entry in index.Packs)
		{
			if (entry == null) continue;
			if (!IsValidPackId(entry.Id) || !seen.Add(entry.Id)) continue;
			if (!IsSafePackFileName(entry.File)) continue;
			if (entry.Sha256 == null || !ShaPattern.IsMatch(entry.Sha256.Trim())) continue;
			entry.Sha256 = entry.Sha256.Trim().ToLowerInvariant();
			if (string.IsNullOrWhiteSpace(entry.DisplayName)) entry.DisplayName = entry.Id;
			entry.ShowsLine ??= "";
			if (entry.EpisodeCount < 0) entry.EpisodeCount = 0;
			if (entry.DataVersion < 1) entry.DataVersion = 1;
			entries.Add(entry);
		}
		return entries;
	}

	/// <summary>Verify downloaded bytes against an index entry's
	/// promised SHA-256 (hex, compared case-insensitively). The
	/// gate every pack download passes before it is decoded,
	/// parsed, or installed.</summary>
	public static bool VerifySha256(byte[] bytes, string? expectedHex)
	{
		if (bytes == null || expectedHex == null) return false;
		var expected = expectedHex.Trim();
		if (!ShaPattern.IsMatch(expected)) return false;
		var actual = Convert.ToHexString(SHA256.HashData(bytes));
		return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Whether the index offers a newer data version of
	/// an installed pack than the one on the device.</summary>
	public static bool UpdateAvailable(PackIndexEntry entry, InstalledPackRecord installed) =>
		entry != null && installed != null && entry.DataVersion > installed.DataVersion;
}

/// <summary>One installed pack's registry record — what the
/// state file remembers about a pack on this device: which data
/// version it is, the SHA-256 its bytes verified against, the
/// file it lives in, and the registration metadata its envelope
/// declared (kept here too so the pack manager can describe an
/// installed pack without loading its dataset).</summary>
public sealed class InstalledPackRecord
{
	public required string Id { get; init; }
	public int DataVersion { get; init; } = 1;
	public string Sha256 { get; init; } = "";
	public string FileName { get; init; } = "";
	public string DisplayName { get; init; } = "";
	public string ShowsLine { get; init; } = "";
}

/// <summary>The outcome of a pack install attempt — one type
/// for the network front-end (PackService) and the Store's
/// install path, so the UI has exactly one result to speak
/// about. Failures carry a plain-language message; nothing is
/// ever half-installed (see Store.InstallPackAsync).</summary>
public sealed class PackInstallResult
{
	public bool Ok { get; private init; }
	public bool WasUpdate { get; private init; }
	public string Message { get; private init; } = "";

	public static PackInstallResult Success(bool wasUpdate) => new()
	{
		Ok = true,
		WasUpdate = wasUpdate,
		Message = wasUpdate ? "Updated." : "Installed.",
	};

	public static PackInstallResult Fail(string message) => new()
	{
		Ok = false,
		Message = message,
	};

	/// <summary>A successful non-install action (a removal, a
	/// restore) with its own plain message.</summary>
	public static PackInstallResult Done(string message) => new()
	{
		Ok = true,
		Message = message,
	};
}
