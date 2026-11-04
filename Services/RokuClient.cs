using System.Xml.Linq;

namespace OneChicago.Services;

public class MediaPlayerStatus
{
	public bool Reachable { get; set; }
	public string? State { get; set; }
	public string? PluginId { get; set; }
	public string? PluginName { get; set; }
	public long? PositionMs { get; set; }

	/// <summary>The playing content's total length, when the
	/// platform reports one. Disney+ does; Prime and Netflix
	/// don't (null there) — the monitor judges their endings
	/// against dataset runtimes (Decision 1G).</summary>
	public long? DurationMs { get; set; }
	public string? Error { get; set; }

	public bool IsPrime => PluginId == Store.PrimeChannelId.ToString();

	/// <summary>Whether the player belongs to the given ECP
	/// channel id — the per-platform form of IsPrime.</summary>
	public bool IsPlugin(string? channelId) => channelId != null && PluginId == channelId;
	public bool IsPlaying => State == "play";
	public bool IsPaused => State == "pause";
}

/// <summary>One channel installed on the TV, from /query/apps:
/// its ECP id and its display name (the name is how the app
/// recognizes the streaming platforms — see PlatformCatalog).</summary>
public sealed record RokuAppInfo(string Id, string Name);

/// <summary>
/// Talks to the user's Roku TV over the home LAN via ECP (port 8060,
/// plain HTTP, no auth). Proven on the original test TV on
/// 2026-11-03: Prime Video reports state and live position via
/// /query/media-player, but no duration and no title.
///
/// Production hardening (audit 1.3): the TV address is re-validated
/// here, before any request URL is built — imported or legacy state
/// can never make the app poll an address that isn't a private /
/// link-local IPv4 address. An invalid stored address produces an
/// honest "not set up" result, never a request. All responses are
/// read with a hard byte cap (audit 1.2).
/// </summary>
public class RokuClient
{
	private readonly Store _store;
	private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };

	public RokuClient(Store store) => _store = store;

	/// <summary>The request base URL, or null when no valid TV
	/// address is set. The ONLY place a Roku URL is built from the
	/// stored address.</summary>
	private string? Base => Store.TryValidateTvAddress(_store.TvIp, out var ip)
		? $"http://{ip}:8060"
		: null;

	private async Task<string?> GetBoundedAsync(string path)
	{
		if (Base is not { } baseUrl) return null;
		return await BoundedRead.GetStringAsync(_http, $"{baseUrl}{path}", BoundedRead.RokuCapBytes);
	}

	public async Task<MediaPlayerStatus> GetMediaPlayerAsync()
	{
		if (Base == null)
			return new MediaPlayerStatus { Reachable = false, Error = "No valid TV address is set. Add your TV in Settings." };
		try
		{
			var xml = await GetBoundedAsync("/query/media-player");
			if (xml == null) return new MediaPlayerStatus { Reachable = false, Error = "No valid TV address is set." };
			var doc = XDocument.Parse(xml);
			var player = doc.Root;
			if (player == null) return new MediaPlayerStatus { Reachable = true };
			var plugin = player.Element("plugin");
			long? ParseMs(string? raw)
			{
				if (string.IsNullOrEmpty(raw)) return null;
				var digits = new string(raw.Where(char.IsDigit).ToArray());
				return long.TryParse(digits, out var v) ? v : null;
			}
			return new MediaPlayerStatus
			{
				Reachable = true,
				State = (string?)player.Attribute("state"),
				PluginId = (string?)plugin?.Attribute("id"),
				PluginName = (string?)plugin?.Attribute("name"),
				PositionMs = ParseMs(player.Element("position")?.Value?.Trim()),
				DurationMs = ParseMs(player.Element("duration")?.Value?.Trim()),
			};
		}
		catch (Exception ex)
		{
			return new MediaPlayerStatus { Reachable = false, Error = ex.Message };
		}
	}

	/// <summary>Channel id of the app currently in the foreground on the TV
	/// (e.g. "13" for Prime Video), from /query/active-app.</summary>
	public async Task<string?> GetActiveAppIdAsync()
	{
		try
		{
			var xml = await GetBoundedAsync("/query/active-app");
			if (xml == null) return null;
			var doc = XDocument.Parse(xml);
			return (string?)doc.Root?.Element("app")?.Attribute("id");
		}
		catch
		{
			return null;
		}
	}

	/// <summary>User-facing device summary for "Check connection":
	/// vendor, model name/number, and Roku OS. The serial number is
	/// a device identifier and NEVER appears in user-facing output
	/// (audit 1.8 / Section 3 item 10).</summary>
	public async Task<string?> GetDeviceInfoSummaryAsync()
	{
		try
		{
			var xml = await GetBoundedAsync("/query/device-info");
			if (xml == null) return null;
			var doc = XDocument.Parse(xml);
			var root = doc.Root;
			if (root == null) return null;
			string V(string n) => root.Element(n)?.Value ?? "";
			return $"{V("vendor-name")} {V("model-name")} (model {V("model-number")}, Roku OS {V("software-version")})";
		}
		catch
		{
			return null;
		}
	}

	public async Task<bool> PostAsync(string path)
	{
		if (Base is not { } baseUrl) return false;
		try
		{
			using var res = await _http.PostAsync($"{baseUrl}{path}", null);
			return res.IsSuccessStatusCode;
		}
		catch
		{
			return false;
		}
	}

	public Task<bool> LaunchPrimeAsync() => PostAsync($"/launch/{Store.PrimeChannelId}");

	/// <summary>Bring a channel to the foreground with no content
	/// parameters (the streaming platforms' fallback step: open
	/// the channel first, then address the episode via /input/).</summary>
	public Task<bool> LaunchChannelAsync(string channelId) => PostAsync($"/launch/{channelId}");

	public Task<bool> SendKeyAsync(string key) => PostAsync($"/keypress/{key}");

	/// <summary>
	/// Ask Prime to play a specific episode by content ID.
	/// Uses /input/ when Prime is already the active app, /launch/ otherwise.
	/// Prime has never documented which IDs it accepts — the guided play
	/// sequence (GuidedPlayer) drives this and verifies via media-player.
	/// </summary>
	public Task<bool> PlayEpisodeAsync(string contentId, bool primeAlreadyActive)
		=> PlayEpisodeAsync(contentId, primeAlreadyActive, "episode");

	public Task<bool> PlayEpisodeAsync(string contentId, bool primeAlreadyActive, string mediaType)
		=> PlayOnChannelAsync(contentId, Store.PrimeChannelId.ToString(), primeAlreadyActive, mediaType);

	/// <summary>The per-platform form of PlayEpisodeAsync
	/// (Decision 1G): address a content ID to any channel, via
	/// /input/ when the channel is already the active app, or
	/// /launch/ when it isn't.</summary>
	public async Task<bool> PlayOnChannelAsync(string contentId, string channelId, bool channelAlreadyActive, string mediaType)
	{
		var q = $"?contentId={Uri.EscapeDataString(contentId)}&mediaType={mediaType}";
		var verb = channelAlreadyActive ? "input" : "launch";
		return await PostAsync($"/{verb}/{channelId}{q}");
	}

	/// <summary>The channels installed on the TV (/query/apps),
	/// or null when the TV can't be asked (no address, off,
	/// unreachable, unparseable answer).</summary>
	public async Task<List<RokuAppInfo>?> GetInstalledAppsAsync()
	{
		try
		{
			var xml = await GetBoundedAsync("/query/apps");
			if (xml == null) return null;
			var doc = XDocument.Parse(xml);
			var apps = new List<RokuAppInfo>();
			foreach (var app in doc.Root?.Elements("app") ?? Enumerable.Empty<XElement>())
			{
				var id = (string?)app.Attribute("id");
				var name = app.Value?.Trim();
				if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
					apps.Add(new RokuAppInfo(id, name));
			}
			return apps;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Discover the streaming platforms' channel ids on
	/// this TV and cache them in the Store (Decision 1G): query
	/// /query/apps, match each platform by its channel name, and
	/// record what changed (a platform whose channel is gone is
	/// forgotten, so link choice stops offering it). Returns true
	/// when the TV answered — the cache then reflects reality —
	/// and false when it couldn't be asked, in which case the
	/// cache is left exactly as it was.</summary>
	public async Task<bool> DiscoverChannelsAsync()
	{
		var apps = await GetInstalledAppsAsync();
		if (apps == null) return false;
		bool changed = false;
		foreach (var platform in PlatformCatalog.All)
		{
			var found = apps.FirstOrDefault(a =>
				string.Equals(a.Name, platform.ChannelName, StringComparison.OrdinalIgnoreCase));
			var known = _store.PlatformChannelKnown(platform.Signifier)
				? _store.ChannelIdForPlatform(platform.Signifier)
				: null;
			if (found?.Id != known)
			{
				_store.SetPlatformChannel(platform.Signifier, found?.Id, save: false);
				changed = true;
			}
		}
		if (changed) _store.SaveSettings();
		return true;
	}
}
