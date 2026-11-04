using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace OneChicago.Services;

public class FoundTv
{
	public required string Ip { get; init; }
	public required string Summary { get; init; }
}

/// <summary>
/// "Find My TV" (Settings): hunts the local network for Roku devices
/// so the user doesn't have to know the TV's current DHCP address.
/// User-initiated only — it runs solely from the Find My TV button.
///
///  1. SSDP: an M-SEARCH for roku:ecp. Production trust rules
///     (audit 1.3): the candidate is the address the packet actually
///     arrived FROM, in preference to the advertised LOCATION host
///     (which anything on the LAN can claim); a candidate counts
///     only if it is a private / link-local IPv4 address, and it is
///     believed only after /query/device-info answers with the
///     fuller Roku field set (serial AND model AND vendor).
///  2. If SSDP finds nothing, probe this phone's own /24 subnet on
///     port 8060 /query/device-info (same belief bar).
///
/// Purely additive: it only REPORTS candidates. Filling the address
/// field still happens by the user tapping a result.
///
/// The SSDP search runs under a Wi-Fi multicast lock
/// (SsdpMulticastLock, below) — the genuine use behind the
/// manifest's multicast-state declaration, which is in turn the
/// connectedDevice foreground service's runtime prerequisite
/// (production Decision 4).
/// </summary>
public static class RokuFinder
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };

	public static async Task<List<FoundTv>> FindAsync(Action<string>? progress = null, CancellationToken ct = default)
	{
		var found = new Dictionary<string, FoundTv>();

		progress?.Invoke("Listening for the TV on the network…");
		foreach (var ip in await SsdpSearchAsync(ct))
		{
			if (found.ContainsKey(ip)) continue;
			if (await ProbeAsync(ip) is { } tv) found[ip] = tv;
		}

		if (found.Count == 0)
		{
			progress?.Invoke("No answer to the shout — checking every address on your Wi-Fi…");
			var prefix = LocalSubnetPrefix();
			if (prefix != null)
			{
				using var gate = new SemaphoreSlim(64);
				var tasks = Enumerable.Range(1, 254).Select(async i =>
				{
					await gate.WaitAsync(ct);
					try { return await ProbeAsync(prefix + i); }
					finally { gate.Release(); }
				});
				foreach (var tv in await Task.WhenAll(tasks))
					if (tv != null) found[tv.Ip] = tv;
			}
		}

		return found.Values.OrderBy(t => t.Ip).ToList();
	}

	private static async Task<List<string>> SsdpSearchAsync(CancellationToken ct)
	{
		var ips = new List<string>();
		// Hold a Wi-Fi multicast lock across the whole M-SEARCH
		// send/receive window: on many chipsets the Wi-Fi driver
		// filters multicast packets unless an app holds one, so
		// SSDP answers can be silently missed without it. This lock
		// is also the real use behind the manifest's multicast-state
		// declaration (the connectedDevice foreground-service
		// prerequisite, production Decision 4). Acquired before the
		// first send and released in the finally below, so no exit
		// path — answers, timeout, cancellation, error — leaks it.
		var multicastLock = SsdpMulticastLock.Acquire();
		try
		{
			using var udp = new UdpClient();
			udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
			var request = Encoding.ASCII.GetBytes(
				"M-SEARCH * HTTP/1.1\r\n" +
				"HOST: 239.255.255.250:1900\r\n" +
				"MAN: \"ssdp:discover\"\r\n" +
				"MX: 2\r\n" +
				"ST: roku:ecp\r\n\r\n");
			var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
			await udp.SendAsync(request, target);
			await udp.SendAsync(request, target);

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(3));
			while (true)
			{
				UdpReceiveResult res;
				try { res = await udp.ReceiveAsync(timeout.Token); }
				catch { break; }
				// Trust the packet's actual source address first —
				// it is much harder to spoof than a header. The
				// advertised LOCATION host is only a fallback, and
				// only when it too is a valid local address.
				var fromIp = res.RemoteEndPoint.Address.ToString();
				if (Store.IsValidTvAddress(fromIp) && !ips.Contains(fromIp))
					ips.Add(fromIp);
				var text = Encoding.ASCII.GetString(res.Buffer);
				foreach (var line in text.Split("\r\n"))
				{
					if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
					var url = line["LOCATION:".Length..].Trim();
					if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
						&& Store.IsValidTvAddress(uri.Host) && !ips.Contains(uri.Host))
						ips.Add(uri.Host);
				}
			}
		}
		catch { /* SSDP is best-effort; the subnet probe is the fallback */ }
		finally { multicastLock.Dispose(); }
		return ips;
	}

	private static async Task<FoundTv?> ProbeAsync(string ip)
	{
		// Candidates must be local addresses before any request is
		// built for them (audit 1.3, same ranges as everywhere else).
		if (!Store.TryValidateTvAddress(ip, out var validIp)) return null;
		try
		{
			var xml = await BoundedRead.GetStringAsync(Http,
				$"http://{validIp}:8060/query/device-info", BoundedRead.RokuCapBytes);
			var root = XDocument.Parse(xml).Root;
			if (root == null) return null;
			string V(string n) => root.Element(n)?.Value ?? "";
			// Believe it only on the fuller Roku field set: serial
			// AND model AND vendor. (The serial is used here, inside
			// the trust check only — it never reaches the summary or
			// any other user-facing output.)
			if (V("serial-number").Length == 0 || V("model-number").Length == 0 || V("vendor-name").Length == 0) return null;
			return new FoundTv
			{
				Ip = validIp,
				Summary = $"{V("vendor-name")} {V("model-name")} (model {V("model-number")}, Roku OS {V("software-version")})".Trim(),
			};
		}
		catch
		{
			return null;
		}
	}

	private static string? LocalSubnetPrefix()
	{
		try
		{
			foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
			{
				if (ni.OperationalStatus != OperationalStatus.Up) continue;
				if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
				foreach (var ua in ni.GetIPProperties().UnicastAddresses)
				{
					if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
					{
						// Probe only the phone's own local subnet: if
						// the phone itself isn't on a private/link-local
						// address, there is no local /24 to probe.
						if (!Store.IsValidTvAddress(ua.Address.ToString())) continue;
						var b = ua.Address.GetAddressBytes();
						return $"{b[0]}.{b[1]}.{b[2]}.";
					}
				}
			}
		}
		catch { /* fall through */ }
		return null;
	}

	/// <summary>A held Wi-Fi multicast lock (see SsdpSearchAsync).
	/// Acquisition is best-effort: if the platform refuses (Wi-Fi
	/// off, an OEM quirk, a non-Android build), the holder is empty
	/// and discovery proceeds exactly as it did before the lock
	/// existed. Dispose releases the lock if it is held.</summary>
	private sealed class SsdpMulticastLock : IDisposable
	{
#if ANDROID
		private readonly Android.Net.Wifi.WifiManager.MulticastLock? _lock;

		private SsdpMulticastLock(Android.Net.Wifi.WifiManager.MulticastLock? l) => _lock = l;
#else
		private SsdpMulticastLock() { }
#endif

		public static SsdpMulticastLock Acquire()
		{
#if ANDROID
			try
			{
				var ctx = Android.App.Application.Context;
				var wifi = ctx.GetSystemService(Android.Content.Context.WifiService)
					as Android.Net.Wifi.WifiManager;
				var l = wifi?.CreateMulticastLock("onechicago-ssdp");
				// Non-reference-counted: one Acquire pairs with the
				// one Release in Dispose.
				l?.SetReferenceCounted(false);
				l?.Acquire();
				return new SsdpMulticastLock(l);
			}
			catch { return new SsdpMulticastLock(null); }
#else
			return new SsdpMulticastLock();
#endif
		}

		public void Dispose()
		{
#if ANDROID
			try { if (_lock is { IsHeld: true }) _lock.Release(); } catch { }
			try { _lock?.Dispose(); } catch { }
#endif
		}
	}
}
