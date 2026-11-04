using System.Xml.Linq;

namespace OneChicago.Services;

public class MediaPlayerStatus
{
	public bool Reachable { get; set; }
	public string? State { get; set; }
	public string? PluginId { get; set; }
	public string? PluginName { get; set; }
	public long? PositionMs { get; set; }
	public string? Error { get; set; }

	public bool IsPrime => PluginId == Store.PrimeChannelId.ToString();
	public bool IsPlaying => State == "play";
	public bool IsPaused => State == "pause";
}

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
			var posRaw = player.Element("position")?.Value?.Trim();
			long? pos = null;
			if (!string.IsNullOrEmpty(posRaw))
			{
				var digits = new string(posRaw.Where(char.IsDigit).ToArray());
				if (long.TryParse(digits, out var p)) pos = p;
			}
			return new MediaPlayerStatus
			{
				Reachable = true,
				State = (string?)player.Attribute("state"),
				PluginId = (string?)plugin?.Attribute("id"),
				PluginName = (string?)plugin?.Attribute("name"),
				PositionMs = pos,
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

	public Task<bool> SendKeyAsync(string key) => PostAsync($"/keypress/{key}");

	/// <summary>
	/// Ask Prime to play a specific episode by content ID.
	/// Uses /input/ when Prime is already the active app, /launch/ otherwise.
	/// Prime has never documented which IDs it accepts — the guided play
	/// sequence (GuidedPlayer) drives this and verifies via media-player.
	/// </summary>
	public Task<bool> PlayEpisodeAsync(string contentId, bool primeAlreadyActive)
		=> PlayEpisodeAsync(contentId, primeAlreadyActive, "episode");

	public async Task<bool> PlayEpisodeAsync(string contentId, bool primeAlreadyActive, string mediaType)
	{
		var q = $"?contentId={Uri.EscapeDataString(contentId)}&mediaType={mediaType}";
		var verb = primeAlreadyActive ? "input" : "launch";
		return await PostAsync($"/{verb}/{Store.PrimeChannelId}{q}");
	}
}
