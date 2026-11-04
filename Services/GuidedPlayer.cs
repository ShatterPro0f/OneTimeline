using OneChicago.Models;

namespace OneChicago.Services;

public class GuidedPlayResult
{
	public bool Playing { get; set; }
	public long? PositionMs { get; set; }

	/// <summary>True when the tap was dropped because another guided
	/// sequence is already running (single-flight guard). Nothing was
	/// sent to the TV for this call.</summary>
	public bool Busy { get; set; }

	/// <summary>True when the target episode was already playing on the
	/// TV and the sequence deliberately sent nothing.</summary>
	public bool AlreadyPlaying { get; set; }

	/// <summary>True when the call was suppressed by the post-success
	/// quiet period (iteration 5): the episode was started successfully
	/// within the last 60 s, so nothing was sent.</summary>
	public bool QuietPeriod { get; set; }
}

/// <summary>
/// The guided "Play" sequence: instead of firing one deep link and hoping,
/// it walks the TV through everything Sam's set actually does, checking
/// the TV's own reports as it goes.
///
/// Facts it is built around (observed on Sam's TV, 2026-11-03):
///  - Launching Prime cold lands on Prime's PROFILE selection menu;
///    a Select keypress picks the highlighted profile.
///  - If Prime is already the active app, no launch/profile steps.
///  - A deep link lands on the episode's detail page (media-player state
///    "close"), not playing — one Select press starts it from there.
///    Proven end-to-end: episode form + that Select started both a Med
///    and a P.D. episode.
///  - Success is verifiable, but only just: media-player reports plugin
///    13, state and a live position — never WHICH episode is playing.
///    Iteration 8 (below) is what that blindness forces on confirmation.
///
/// Iteration 2 (Sam's reports from the first on-TV runs):
///  - SINGLE-FLIGHT: only one sequence runs at a time, process-wide.
///    Extra taps are dropped, never queued —
///    v1.1 queued one full sequence per tap, so repeated taps kept
///    re-opening the episode after it had already started.
///  - ALREADY-PLAYING GUARD: before sending anything, the TV is polled.
///    If the target episode is the last one this app started and it is
///    playing and advancing, the sequence ends with "Already playing"
///    and sends NOTHING (protecting Sam's resume point). If that same
///    episode is merely paused on the TV, a Play keypress resumes it
///    instead of re-sending the link. If something DIFFERENT is playing,
///    the warm path is used (link via /input/, no launch/profile).
///  - ADAPTIVE TIMING: fixed sleeps (6 s / 5 s / 10 s) replaced with 1 s
///    polling loops against active-app and media-player, proceeding the
///    moment the TV is ready. Worst case stays bounded (~50 s, only
///    when nothing ever plays); a cold start typically lands near
///    12–15 s and a warm start near 5 s.
///  - PROFILE TOGGLE (iteration 3): the two profile-pick Selects above
///    are gated on Store.ProfileSelectEnabled (default ON — identical
///    behaviour). OFF omits only those keypresses, nothing else.
///
/// Iteration 5 (Sam's rollover glitch: after a perfect Fire→Med autoplay
/// handoff, the TV flashed Prime's PROFILE screen once and restarted
/// Med, untouched):
///  - PLAYER TRUST: media-player outranks active-app. If the player
///    reports Prime in state=play, no /launch/ is ever sent — checked
///    both at the Step 1 launch decision and again before the Step 4
///    launch form (the only two launch sites). A launch restarts
///    Prime's UI at its profile gate; that is exactly the flash Sam saw.
///  - QUIET PERIOD: a confirmed successful start is remembered for
///    60 s. A repeat sequence for the same episode in that window,
///    reaching the point of sending commands with the TV state
///    ambiguous, is vetoed and sends nothing (the guard above still
///    gets first say for confirmed playing/paused states).
///
/// Iteration 8 (Sam's background-autoplay incident, 2026-11-03: Med 1x01
/// ended, the app checked it off, counted down, ran this sequence for
/// P.D. 3x08 — and then displayed P.D. as playing with a live, advancing
/// position, while the TV was in fact playing Med 1x02):
///  - CONFIRMATION REQUIRES APPLIED: the old wait accepted "state=play +
///    position advanced between two polls" as success. That playback
///    need not be ours at all — a link ignored while something else is
///    playing/advancing confirms an episode the sequence never started,
///    and a blind page-Select on Prime's END SCREEN (also state "close",
///    next-episode card focused) starts Prime's pick, which then
///    "confirms" the same way. Every step now snapshots the player
///    BEFORE sending its link (the baseline), and the wait
///    confirms only playback that is demonstrably NEW relative to that
///    baseline: a non-play Prime state observed after the link, or the
///    position observed to DROP to near the start (&lt; 90 s) from a
///    much higher reading, then advance. Continuous play that simply
///    carries over the pre-link stream never confirms.
///  - PAGE-SELECT GATING: on warm starts (this sequence did not open
///    Prime itself), the page Select is sent only for a page that first
///    appeared after the step's link was sent, or after the state left
///    "close" and returned. A page already showing before the link and
///    persisting is treated as foreign — never selected. On cold starts
///    and the displacement retry (this sequence launched Prime), every
///    page is on our own journey and the original behaviour applies.
///  - ONE DISPLACEMENT ESCALATION: when a step concludes its link was
///    ignored because foreign content is playing (ForeignPlaying) or a
///    foreign page is sitting open (ForeignPage), the sequence performs
///    ONE decisive reset — Home, wait for Prime to leave the
///    foreground, then the full cold path (launch, profile Select,
///    /input/ link, strict confirmation) — in place of the Step 4
///    launch form where they overlap. If the reset also fails to
///    confirm, the sequence ends in an honest failure and records
///    NOTHING as started: a failed switch must look failed, never
///    "as though it had switched".
///
/// Iteration 9 — MULTI-PLATFORM (Decision 1G, on the platform
/// evidence of 2026-11-04): the sequence now takes a PlayTarget —
/// a platform, its content ID, its TV channel id and the entry's
/// mediaType — resolved from the episode's links by
/// ResolveTargetAsync. The PRIME sequence above is untouched in
/// behaviour (it is the proven path; its plugin checks now read
/// through the sequence's match channel, which for Prime is
/// channel 13 — the same test). Netflix and Disney+ run a
/// simpler sibling sequence built only from steps Sam's TV
/// proved: cold = /launch/ form of the deep link; warm = /input/
/// form (the route Sam's Netflix episode test used); a ignored
/// warm link escalates to the /launch/ form, which displaces;
/// a cold launch that never brings the channel forward triggers
/// one channel re-discovery and retry, then the plain-launch +
/// /input/ fallback. Strict confirmation (WaitAfterLinkAsync)
/// is shared, with page Selects DISABLED for these platforms —
/// neither has a proven detail-page state, and no keypress is
/// sent blind. PROFILE SELECTS ARE PRIME-ONLY: whether Netflix
/// or Disney+ present a profile gate on a cold launch is
/// untested on Sam's TV, so the streaming sequence sends none;
/// if a gate is up, the sequence fails honestly and a warm
/// retry (after the user picks a profile with the remote)
/// works. Disney+ needs no profile step in Sam's tests — its
/// /launch/ proofs went straight to the content.
/// </summary>
public class GuidedPlayer
{
	private readonly Store _store;
	private readonly RokuClient _roku;

	private int _running; // 0 = idle, 1 = a sequence is running

	// Post-success quiet period (iteration 5): which episode this player
	// last actually got playing, and when. For 60 s afterwards no new
	// sequence for that episode may send commands — see InQuietPeriod,
	// the veto in PlayCoreAsync, and AutoplayCoordinator.
	private string _lastSuccessKey = "";
	private DateTimeOffset _lastSuccessAt = DateTimeOffset.MinValue;

	// The channel the RUNNING sequence is addressing (iteration
	// 9): WaitAfterLinkAsync judges the player against this
	// channel's plugin id. For Prime it is channel 13 — exactly
	// the old IsPrime test. Set at each sequence's start; the
	// player is single-flight, so one field is safe.
	private string _matchPluginId = Store.PrimeChannelId.ToString();

	private bool Matches(MediaPlayerStatus s) => s.PluginId == _matchPluginId;

	/// <summary>True while the 60-second post-success quiet period for
	/// this episode key is still running.</summary>
	public bool InQuietPeriod(string episodeKey) =>
		!string.IsNullOrEmpty(episodeKey)
		&& episodeKey == _lastSuccessKey
		&& DateTimeOffset.UtcNow - _lastSuccessAt < TimeSpan.FromSeconds(60);

	public GuidedPlayer(Store store, RokuClient roku)
	{
		_store = store;
		_roku = roku;
	}

	public bool IsRunning => Volatile.Read(ref _running) == 1;

	/// <summary>Raised when a sequence starts or finishes, so every Play
	/// button in the app can show the shared "Working…" state.</summary>
	public event Action? RunStateChanged;

	/// <summary>Resolve an episode's playable target (Decision
	/// 1G + My Services, 2026-11-04): the first of its effective
	/// links (dataset order = the pack author's priority) whose
	/// platform the user HAS (My Services) and whose channel is
	/// installed on the TV. The Store's cached channel knowledge
	/// is tried first; if nothing matches, the TV is asked
	/// (/query/apps discovery) and the choice retried. When the
	/// TV can't be asked at all and the episode carries a Prime
	/// link on a service the user has, Prime's proven channel 13
	/// is aimed at exactly as the app always has. An episode
	/// whose links all sit on services the user doesn't have
	/// resolves to NoSelectedService — for this user it is a
	/// pure checklist entry. Every outcome is a stated reason,
	/// not a silent failure.</summary>
	public async Task<PlayResolution> ResolveTargetAsync(Episode ep)
	{
		var allLinks = _store.LinksFor(ep);
		if (allLinks.Count == 0) return PlayResolution.NoLinks();
		var links = _store.WatchLinksFor(ep);
		if (links.Count == 0)
		{
			var unselectedNames = allLinks
				.Select(l => PlatformCatalog.Find(l.Platform)?.DisplayName ?? l.Platform)
				.Distinct()
				.ToList();
			return PlayResolution.NoSelectedService(unselectedNames);
		}
		var names = links
			.Select(l => PlatformCatalog.Find(l.Platform)?.DisplayName ?? l.Platform)
			.Distinct()
			.ToList();

		var chosen = PlayLinkChoice.Choose(links, _store.KnownInstalledPlatforms);
		if (chosen == null)
		{
			bool answered = await _roku.DiscoverChannelsAsync();
			chosen = PlayLinkChoice.Choose(links, _store.KnownInstalledPlatforms);
			if (chosen == null)
			{
				var primeLink = links.FirstOrDefault(l => l.Platform == PlatformCatalog.PrimeSignifier);
				if (!answered && primeLink != null)
					return PlayResolution.Ready(TargetFor(ep, primeLink));
				return answered
					? PlayResolution.NotInstalled(names)
					: PlayResolution.Unreachable(names);
			}
		}
		return PlayResolution.Ready(TargetFor(ep, chosen));
	}

	private PlayTarget TargetFor(Episode ep, EpisodeLink link)
	{
		var platform = PlatformCatalog.Find(link.Platform)!;
		// Non-null for any chosen link: choice requires the
		// platform to be known-installed (or Prime, whose
		// fallback this is).
		var channelId = _store.ChannelIdForPlatform(platform.Signifier)
			?? Store.PrimeChannelId.ToString();
		return new PlayTarget(platform, link.Id, channelId, ep.DeepLinkMediaType);
	}

	public async Task<GuidedPlayResult> PlayAsync(Episode? episode, PlayTarget target, string episodeLabel)
	{
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
		{
			// Single-flight: drop the tap, never queue it. A queued
			// sequence replaying later is exactly what yanked Sam's
			// episode back to the menu in v1.1.
			return new GuidedPlayResult { Busy = true };
		}
		RunStateChanged?.Invoke();
		try
		{
			return target.Platform.IsPrime
				? await PlayPrimeAsync(episode, target, episodeLabel)
				: await PlayStreamingAsync(episode, target, episodeLabel);
		}
		finally
		{
			Volatile.Write(ref _running, 0);
			RunStateChanged?.Invoke();
		}
	}

	/// <summary>The Prime sequence — iterations 1–8 above,
	/// behaviour unchanged. The target's channel is Prime's 13
	/// and its ID faces the same strict GTI normalization as
	/// every Prime ID before it.</summary>
	private async Task<GuidedPlayResult> PlayPrimeAsync(Episode? episode, PlayTarget target, string episodeLabel)
	{
		_matchPluginId = Store.PrimeChannelId.ToString();
		var id = Store.NormalizeContentId(target.ContentId);

		if (string.IsNullOrEmpty(id))
		{
			return new GuidedPlayResult { Playing = false };
		}

		bool sameAsLastStarted = episode != null && _store.LastStartedEpisodeKey == episode.Key;

		GuidedPlayResult Hit(GuidedPlayResult r)
		{
			if (episode != null && r.Playing)
			{
				_lastSuccessKey = episode.Key;
				_lastSuccessAt = DateTimeOffset.UtcNow;
			}
			if (episode != null && r.PositionMs is long hp)
				_store.NoteEpisodeStarted(episode, hp, PlatformCatalog.PrimeSignifier);
			return r;
		}

		// Step 0 — already-playing guard: ask the TV what it's doing
		// before sending it anything at all.
		var g1 = await _roku.GetMediaPlayerAsync();
		await Task.Delay(1000);
		var g2 = await _roku.GetMediaPlayerAsync();

		bool advancing = g1.IsPrime && g2.IsPrime
			&& g1.State == "play" && g2.State == "play"
			&& g1.PositionMs is long gp1 && g2.PositionMs is long gp2 && gp2 > gp1;

		bool primeWarm;
		if (advancing && g2.PositionMs is long nowPos)
		{
			if (sameAsLastStarted)
			{
				_store.NoteLastStartedPosition(nowPos);

				return new GuidedPlayResult { Playing = true, AlreadyPlaying = true, PositionMs = nowPos };
			}

			primeWarm = true;
		}
		else if (sameAsLastStarted && g2.IsPrime && g2.State == "pause" && g2.PositionMs is long pausedPos)
		{
			// Same episode, paused on the TV: resume it with a Play
			// keypress. Re-sending the deep link here could restart the
			// episode and cost Sam his place.

			await _roku.SendKeyAsync("Play");
			if (await WaitAfterLinkAsync(g2, gatePageSelect: false, 6, 0, allowPageSelect: false) is { Hit: { } resumed })
				return Hit(resumed);

			return new GuidedPlayResult { Playing = false };
		}
		else if (episode != null && InQuietPeriod(episode.Key))
		{
			// Quiet-period veto (iteration 5): this episode was started
			// successfully within the last minute, and the guard above
			// couldn't positively confirm it (e.g. it's still buffering,
			// so its position isn't advancing yet). Sending anything —
			// above all a launch, which dumps Prime back at its profile
			// gate — would wreck a start that already worked. The guard
			// branches above get first say (confirmed playing → Already
			// playing; confirmed paused → resume), so reaching here means
			// the TV state is ambiguous — exactly when restraint is right.

			return new GuidedPlayResult { Playing = false, QuietPeriod = true };
		}
		else
		{
			// Step 1 — get Prime open, past the profile gate if it was cold.
			// Trust the player over the app check (iteration 5): if
			// media-player says Prime is playing right now, NEVER launch.
			// A launch restarts Prime's UI at its profile screen, so a
			// flaky or stale active-app read must never be enough to
			// justify one while something is playing.
			var fresh = await _roku.GetMediaPlayerAsync();
			bool playerSaysPlaying = fresh.IsPrime && fresh.State == "play";
			var activeId = playerSaysPlaying
				? Store.PrimeChannelId.ToString()
				: await _roku.GetActiveAppIdAsync();
			primeWarm = activeId == Store.PrimeChannelId.ToString();
			if (!primeWarm)
			{
				if (!await _roku.LaunchPrimeAsync())
				{

					return new GuidedPlayResult { Playing = false };
				}
				// Wait for Prime to actually come to the foreground
				// instead of sleeping a fixed 6 s.
				for (int i = 0; i < 6; i++)
				{
					if (await _roku.GetActiveAppIdAsync() == Store.PrimeChannelId.ToString()) break;
					await Task.Delay(1000);
				}
				await Task.Delay(1000); // brief settle before the profile gate

				// Settings toggle (default ON = shipped behaviour): OFF
				// skips only this keypress — every other step, wait and
				// check in the sequence is unchanged.
				if (_store.ProfileSelectEnabled)
					await _roku.SendKeyAsync("Select");
				await Task.Delay(2000);
			}
		}

		bool wasCold = !primeWarm;

		// Page-Select gate (iteration 8): armed only when this sequence
		// did NOT open Prime itself. When Prime was already open, a page
		// sitting there before our link may be a foreign page (a finished
		// episode's end screen) and must never be Select-ed blind. When
		// we launched Prime, every page that shows up is on our journey.
		bool gatePageSelect = !wasCold;

		// The one displacement escalation (iteration 8): usable at most
		// once per sequence, and terminal — its result ends the sequence
		// either way, because it replaces the remaining escalation ladder
		// (including the Step 4 launch form) for the foreign cases.
		bool displacementUsed = false;
		async Task<GuidedPlayResult?> TryDisplacementAsync()
		{
			displacementUsed = true;
			return await DisplaceAndRetryAsync(id);
		}

		// Step 2 — the episode deep link, /input/ form (Prime is open now),
		// judged strictly against the pre-link baseline: if Prime parks on
		// OUR episode's page (state "close"), one Select starts it.

		var b2 = await _roku.GetMediaPlayerAsync();
		await _roku.PlayEpisodeAsync(id, primeAlreadyActive: true, mediaType: "episode");
		var w2 = await WaitAfterLinkAsync(b2, gatePageSelect, 8, 5);
		if (w2.Hit is { } hit1) return Hit(hit1);
		if (w2.IsForeign && !displacementUsed)
		{
			if (await TryDisplacementAsync() is { } dHit1) return Hit(dHit1);

			return new GuidedPlayResult { Playing = false };
		}

		// Step 3 — same ID, labelled as a movie.

		var b3 = await _roku.GetMediaPlayerAsync();
		await _roku.PlayEpisodeAsync(id, primeAlreadyActive: true, mediaType: "movie");
		var w3 = await WaitAfterLinkAsync(b3, gatePageSelect, 6, 4);
		if (w3.Hit is { } hit2) return Hit(hit2);
		if (w3.IsForeign && !displacementUsed)
		{
			if (await TryDisplacementAsync() is { } dHit2) return Hit(dHit2);

			return new GuidedPlayResult { Playing = false };
		}

		// Step 4 — cold-start only: the /launch/ form of the deep link,
		// then the profile Select, then the same adaptive wait.
		if (wasCold)
		{
			// Player-trust re-check (iteration 5): the /input/ forms above
			// may in fact have started the episode without WaitAfterLink
			// being able to confirm it — its confirmation needs the
			// position to ADVANCE between two polls inside a bounded
			// window, and a freshly started stream can sit at state=play
			// with a frozen position while it buffers (this is the
			// leading explanation for Sam's rollover glitch: Med had
			// started, the sequence couldn't confirm it, and this launch
			// form then dumped Prime back at its profile screen before
			// bringing Med back). If the player says Prime is playing
			// now, the start worked — declare success, never relaunch.
			var now = await _roku.GetMediaPlayerAsync();
			if (now.IsPrime && now.State == "play" && now.PositionMs is long cp)
			{

				return Hit(new GuidedPlayResult { Playing = true, PositionMs = cp });
			}

			await _roku.PlayEpisodeAsync(id, primeAlreadyActive: false, mediaType: "episode");
			for (int i = 0; i < 6; i++)
			{
				if (await _roku.GetActiveAppIdAsync() == Store.PrimeChannelId.ToString()) break;
				await Task.Delay(1000);
			}
			await Task.Delay(1000);

			if (_store.ProfileSelectEnabled)
				await _roku.SendKeyAsync("Select");
			await Task.Delay(2000);
			// The player-trust snapshot doubles as this step's baseline:
			// it was read immediately before the launch form was sent.
			if (await WaitAfterLinkAsync(now, gatePageSelect: false, 5, 3) is { Hit: { } hit3 }) return Hit(hit3);
		}

		return new GuidedPlayResult { Playing = false };
	}

	/// <summary>
	/// The Netflix / Disney+ sequence (iteration 9) — built only
	/// from steps proven on Sam's TV (platform-test-findings):
	///  - Step 0 is Prime's already-playing guard, read against
	///    this platform's channel: same episode advancing → send
	///    nothing; same episode paused → one Play keypress resumes.
	///  - WARM (the platform is the foreground app, or its player
	///    is playing): the /input/ form first — the route Sam's
	///    Netflix episode test proved — judged by the same strict
	///    confirmation as Prime. If it's ignored, the /launch/
	///    form follows: launching displaces whatever the channel
	///    is showing.
	///  - COLD: the /launch/ form carries the episode straight in
	///    (every Disney+ proof used it). If the channel never
	///    comes to the foreground, the cached channel id may be
	///    stale: re-discover once and retry at the fresh id. Last
	///    resort: plain launch, wait for the foreground, /input/.
	///  - NO profile Select and NO page Select anywhere: neither
	///    platform has a proven gate/page state on Sam's TV, and
	///    no keypress is sent blind (see the class doc).
	/// </summary>
	private async Task<GuidedPlayResult> PlayStreamingAsync(Episode? episode, PlayTarget target, string episodeLabel)
	{
		var platform = target.Platform;
		var id = PlatformIdRules.Normalize(platform.Signifier, target.ContentId);
		if (!PlatformIdRules.IsValid(platform.Signifier, id))
		{
			return new GuidedPlayResult { Playing = false };
		}
		string channelId = target.ChannelId;
		_matchPluginId = channelId;

		bool sameAsLastStarted = episode != null
			&& _store.LastStartedEpisodeKey == episode.Key
			&& _store.LastStartedPlatform == platform.Signifier;

		GuidedPlayResult Hit(GuidedPlayResult r)
		{
			if (episode != null && r.Playing)
			{
				_lastSuccessKey = episode.Key;
				_lastSuccessAt = DateTimeOffset.UtcNow;
			}
			if (episode != null && r.PositionMs is long hp)
				_store.NoteEpisodeStarted(episode, hp, platform.Signifier);
			return r;
		}

		// Step 0 — already-playing guard (Prime's shape, this
		// platform's channel).
		var g1 = await _roku.GetMediaPlayerAsync();
		await Task.Delay(1000);
		var g2 = await _roku.GetMediaPlayerAsync();

		bool advancing = Matches(g1) && Matches(g2)
			&& g1.State == "play" && g2.State == "play"
			&& g1.PositionMs is long sp1 && g2.PositionMs is long sp2 && sp2 > sp1;

		if (advancing && g2.PositionMs is long nowPos)
		{
			if (sameAsLastStarted)
			{
				_store.NoteLastStartedPosition(nowPos);
				return new GuidedPlayResult { Playing = true, AlreadyPlaying = true, PositionMs = nowPos };
			}
			// Something else is playing on this platform; the
			// warm path below displaces it with our link.
		}
		else if (sameAsLastStarted && Matches(g2) && g2.State == "pause" && g2.PositionMs is long pausedPos)
		{
			// Same episode, paused: resume with a Play keypress —
			// re-sending the link could restart it and cost the
			// resume point (Prime's rule, same reasoning).
			await _roku.SendKeyAsync("Play");
			if (await WaitAfterLinkAsync(g2, gatePageSelect: false, 6, 0, allowPageSelect: false) is { Hit: { } resumed })
				return Hit(resumed);
			return new GuidedPlayResult { Playing = false };
		}
		else if (episode != null && InQuietPeriod(episode.Key))
		{
			return new GuidedPlayResult { Playing = false, QuietPeriod = true };
		}

		// Warm or cold? Player trust, as with Prime: if this
		// platform's player says it's playing, the channel is up
		// whatever active-app claims.
		var fresh = await _roku.GetMediaPlayerAsync();
		bool playerSaysPlaying = Matches(fresh) && fresh.State == "play";
		var activeId = playerSaysPlaying ? channelId : await _roku.GetActiveAppIdAsync();
		bool warm = activeId == channelId;

		if (warm)
		{
			var b = await _roku.GetMediaPlayerAsync();
			await _roku.PlayOnChannelAsync(id, channelId, channelAlreadyActive: true, target.MediaType);
			var w = await WaitAfterLinkAsync(b, gatePageSelect: true, 8, 0, allowPageSelect: false);
			if (w.Hit is { } hitW) return Hit(hitW);

			// The launch form: displaces whatever the channel is
			// showing when a warm link is ignored.
			var b2 = await _roku.GetMediaPlayerAsync();
			await _roku.PlayOnChannelAsync(id, channelId, channelAlreadyActive: false, target.MediaType);
			var w2 = await WaitAfterLinkAsync(b2, gatePageSelect: false, 8, 0, allowPageSelect: false);
			if (w2.Hit is { } hitW2) return Hit(hitW2);
			return new GuidedPlayResult { Playing = false };
		}

		// Cold: the launch form.
		var bc = await _roku.GetMediaPlayerAsync();
		await _roku.PlayOnChannelAsync(id, channelId, channelAlreadyActive: false, target.MediaType);
		var wc = await WaitAfterLinkAsync(bc, gatePageSelect: false, 10, 0, allowPageSelect: false);
		if (wc.Hit is { } hitC) return Hit(hitC);

		// The channel never came forward — the cached id may be
		// stale (channels move when apps are reinstalled). One
		// re-discovery and one retry at the fresh id.
		if (await _roku.GetActiveAppIdAsync() != channelId && await _roku.DiscoverChannelsAsync())
		{
			var freshId = _store.ChannelIdForPlatform(platform.Signifier);
			if (!string.IsNullOrEmpty(freshId) && freshId != channelId)
			{
				channelId = freshId;
				_matchPluginId = channelId;
				var br = await _roku.GetMediaPlayerAsync();
				await _roku.PlayOnChannelAsync(id, channelId, channelAlreadyActive: false, target.MediaType);
				var wr = await WaitAfterLinkAsync(br, gatePageSelect: false, 10, 0, allowPageSelect: false);
				if (wr.Hit is { } hitR) return Hit(hitR);
			}
		}

		// Final fallback: open the channel plainly, wait for the
		// foreground, then the /input/ form.
		if (!await _roku.LaunchChannelAsync(channelId))
		{
			return new GuidedPlayResult { Playing = false };
		}
		for (int i = 0; i < 6; i++)
		{
			if (await _roku.GetActiveAppIdAsync() == channelId) break;
			await Task.Delay(1000);
		}
		await Task.Delay(1000);
		var bf = await _roku.GetMediaPlayerAsync();
		await _roku.PlayOnChannelAsync(id, channelId, channelAlreadyActive: true, target.MediaType);
		var wf = await WaitAfterLinkAsync(bf, gatePageSelect: false, 8, 0, allowPageSelect: false);
		if (wf.Hit is { } hitF) return Hit(hitF);
		return new GuidedPlayResult { Playing = false };
	}

	/// <summary>The outcome of one post-link wait (iteration 8).</summary>
	private enum LinkOutcome
	{
		/// <summary>Playback confirmed as demonstrably ours (strict rules).</summary>
		Confirmed,
		/// <summary>The deadline passed with nothing conclusive either way.</summary>
		Unconfirmed,
		/// <summary>The link was ignored while foreign content kept
		/// playing straight through it — grounds for displacement.</summary>
		ForeignPlaying,
		/// <summary>The link was ignored while a page that pre-dates it
		/// sat open — grounds for displacement.</summary>
		ForeignPage,
	}

	private sealed record LinkWaitResult(GuidedPlayResult? Hit, LinkOutcome Outcome)
	{
		public bool IsForeign => Outcome is LinkOutcome.ForeignPlaying or LinkOutcome.ForeignPage;
	}

	/// <summary>
	/// Poll media-player every ~1 s until the episode is confirmed playing
	/// or the deadline passes.
	///
	/// Confirmation (iteration 8) needs BOTH:
	///  - playback: Prime (plugin 13), state=play, and the position
	///    ADVANCING between two polls; and
	///  - novelty: evidence the playback is NEW relative to the pre-link
	///    baseline — (a) a non-play Prime state (page/stop/pause) was
	///    observed after the link, or (b) the position was observed to
	///    DROP to near the start (&lt; 90 s) from a much higher reading
	///    (baseline included), then advance. A transition from
	///    not-playing to playing is caught by (a)/(b) in observed form:
	///    the not-playing state is seen on a poll after the link, or the
	///    play that follows starts near zero. Continuous play+advancing
	///    that simply carries over the pre-link stream (position
	///    continuing from a high value with no intervening state) never
	///    confirms — the wait ends ForeignPlaying.
	///
	/// If Prime parks on a page (state "close") for ~3 s and page Selects
	/// are allowed, one Select is sent and the deadline extends by
	/// afterSelectCapSeconds — but only when the page is plausibly OURS.
	/// With the gate armed (gatePageSelect: warm starts), a page is ours
	/// only if it was first observed after this step's link was sent, or
	/// after the state had left "close" and come back. A page that was
	/// already the state BEFORE the link and persists is foreign (a
	/// finished episode's end screen looks exactly like an episode
	/// page): no Select is sent, and if nothing else resolves, the wait
	/// ends ForeignPage.
	/// </summary>
	private async Task<LinkWaitResult> WaitAfterLinkAsync(
		MediaPlayerStatus baseline, bool gatePageSelect,
		int firstCapSeconds, int afterSelectCapSeconds, bool allowPageSelect = true)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(firstCapSeconds);
		long? prevPos = null;
		// High-water position for reset (drop) detection — seeded with
		// the baseline's position, so a drop relative to what was playing
		// BEFORE the link counts even on the first post-link poll.
		long? maxPos = Matches(baseline) ? baseline.PositionMs : null;
		bool novel = false;
		bool sawReset = false;
		bool advancingWithoutNovelty = false;
		bool closeObserved = false;
		int closeStuckSeconds = 0;
		bool pageSelectSent = false;
		bool pageIsOurs = !gatePageSelect;
		string? prevState = Matches(baseline) ? baseline.State : null;

		while (DateTimeOffset.UtcNow < deadline)
		{
			var s = await _roku.GetMediaPlayerAsync();

			if (Matches(s) && s.State == "play" && s.PositionMs is long p)
			{
				// Reset evidence: a DROP to near the start from a much
				// higher reading. A foreign stream that merely started
				// near zero before the link and keeps rising shows no
				// drop — maxPos climbs with it step for step — so it can
				// never manufacture this evidence.
				if (p < 90_000 && maxPos is long mx && mx - p > 120_000)
					sawReset = true;
				if (maxPos is not long curMax || p > curMax) maxPos = p;

				if (prevPos is long pp && p > pp)
				{
					if (novel || sawReset)
					{

						return new LinkWaitResult(
							new GuidedPlayResult { Playing = true, PositionMs = p },
							LinkOutcome.Confirmed);
					}
					advancingWithoutNovelty = true;
				}
				prevPos = p;
				closeStuckSeconds = 0;
			}
			else if (Matches(s) && s.State == "close")
			{
				novel = true; // (a): a non-play Prime state seen after the link
				closeObserved = true;
				prevPos = null;
				// The page is plausibly ours only if it first appeared
				// after the link — i.e. the previously observed state
				// (the baseline counts) was something else.
				if (prevState != "close") pageIsOurs = true;
				if (allowPageSelect && pageIsOurs && !pageSelectSent)
				{
					closeStuckSeconds++;
					if (closeStuckSeconds >= 3)
					{
						pageSelectSent = true;

						await _roku.SendKeyAsync("Select");
						deadline = DateTimeOffset.UtcNow.AddSeconds(afterSelectCapSeconds);
						closeStuckSeconds = 0;
					}
				}
				else
				{
					closeStuckSeconds = 0;
				}
			}
			else
			{
				// Pause/stop/other-plugin/no-answer polls. A non-play
				// Prime state observed after the link is novelty
				// evidence (a); play-without-a-position is not.
				if (Matches(s) && s.State != null && s.State != "play")
					novel = true;
				closeStuckSeconds = 0;
				if (s.State != "play") prevPos = null;
			}

			prevState = Matches(s) ? s.State : null;

			if (DateTimeOffset.UtcNow >= deadline) break;
			await Task.Delay(1000);
		}

		if (advancingWithoutNovelty && !novel && !sawReset
			&& Matches(baseline) && baseline.State == "play")
		{

			return new LinkWaitResult(null, LinkOutcome.ForeignPlaying);
		}
		if (gatePageSelect && Matches(baseline) && baseline.State == "close"
			&& closeObserved && !pageSelectSent)
		{

			return new LinkWaitResult(null, LinkOutcome.ForeignPage);
		}
		return new LinkWaitResult(null, LinkOutcome.Unconfirmed);
	}

	/// <summary>
	/// The displacement escalation (iteration 8): when a step's link was
	/// ignored BECAUSE foreign content owns the screen — something else
	/// playing straight through the link, or a foreign page sitting
	/// open — no further link form will fix it, so reset decisively:
	/// Home (exits Prime's player/page to the Roku home), wait for Prime
	/// to leave the foreground, then the full cold path — launch Prime,
	/// profile Select per the toggle, /input/ link — and judge the
	/// result by the same strict confirmation as any other step. A null
	/// return means the episode could NOT be confirmed even after the
	/// reset; the caller ends the sequence in honest failure and records
	/// nothing as started.
	/// </summary>
	private async Task<GuidedPlayResult?> DisplaceAndRetryAsync(string id)
	{
		await _roku.SendKeyAsync("Home");
		for (int i = 0; i < 8; i++)
		{
			var appId = await _roku.GetActiveAppIdAsync();
			if (appId != null && appId != Store.PrimeChannelId.ToString()) break;
			await Task.Delay(1000);
		}

		if (!await _roku.LaunchPrimeAsync())
		{
			return null;
		}
		// Wait for Prime to actually come to the foreground.
		for (int i = 0; i < 6; i++)
		{
			if (await _roku.GetActiveAppIdAsync() == Store.PrimeChannelId.ToString()) break;
			await Task.Delay(1000);
		}
		await Task.Delay(1000); // brief settle before the profile gate

		if (_store.ProfileSelectEnabled)
			await _roku.SendKeyAsync("Select");
		await Task.Delay(2000);

		var baseline = await _roku.GetMediaPlayerAsync();

		await _roku.PlayEpisodeAsync(id, primeAlreadyActive: true, mediaType: "episode");
		// Gate off: this sequence just launched Prime itself, so any
		// page that appears is on our own journey (same rule as a cold
		// start).
		var w = await WaitAfterLinkAsync(baseline, gatePageSelect: false, 8, 5);
		return w.Hit;
	}

	public static string FormatPosition(long ms)
	{
		var t = TimeSpan.FromMilliseconds(ms);
		return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
	}
}
