using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// Polls the TV while the app is in the foreground — or, since
/// iteration 6, whenever the background-mode foreground service is
/// running (App.MonitorServiceActive) — and turns the raw player
/// status into tracker behaviour:
///
///  - a live "Now watching" reading for the Up Next episode, and
///  - auto check-off when Sam actually watches an episode through.
///
/// Auto check-off rules (conservative on purpose):
///  - Prime (channel 13) must be the active plugin, and the position must
///    have been seen ADVANCING in state=play for the current Up Next
///    episode — a paused or static position never checks anything off.
///  - Trigger A: position reaches >= 92% of the episode's TVmaze runtime.
///    (Iteration 3: that fraction is a Settings parameter — "balanced"
///    is the shipped 92% exactly; "early" uses 85%; "credits" disables
///    Trigger A so only Trigger B can check an episode off. Trigger B
///    itself is never re-parameterised.)
///  - Trigger B (end-of-playback): position had reached >= 65% of the
///    runtime and then the episode ends — position resets near zero, the
///    player leaves play/pause, or Prime stops being the active app.
///    (Prime gives no duration and TVmaze lists the 60-minute broadcast
///    slot rather than the ~42-minute streamed length, so the end signal
///    is what catches most real finishes.)
///    (Iteration 7.1: the end signal must be SUSTAINED — observed on two
///    consecutive polls. A single transient reading no longer counts:
///    when playback resumes after an external pause (TV remote), the
///    media-player can flicker for a poll — position near zero while
///    the stream re-buffers, or a brief "close" as the player reopens —
///    and one such poll under the old rule checked the episode off and
///    autoplay jumped on. A ~4 s grace window after any observed
///    pause -> play transition additionally suppresses end signals, and
///    the first playing position after a resume re-baselines the
///    session if it sits far below the high-water mark — a restart-like
///    reading is a new session, not evidence the old one ended.)
///  - After any auto check-off, no further auto check-off can happen until
///    the position has been seen back near the start (a reset) — so one
///    polling session can never run away checking off episode after
///    episode Sam didn't watch.
///
/// </summary>
public class RokuMonitor
{
	private readonly Store _store;
	private readonly RokuClient _roku;
	private CancellationTokenSource? _cts;

	public MediaPlayerStatus? Last { get; private set; }
	public DateTimeOffset LastPollAt { get; private set; }

	public event Action? StatusChanged;
	public event Action<Episode>? EpisodeAutoChecked;

	// Session tracking for the current Up Next episode.
	private string? _trackedKey;
	private bool _sawAdvancing;
	private long _lastPosition;
	private long _maxPosition;
	private bool _disarmed; // true after an auto check-off until a reset is seen

	// Iteration 7.1 — Trigger B hardening (see class doc):
	// _endSignalStreak counts CONSECUTIVE polls presenting an end
	// signal; Trigger B requires 2. _prevState spots pause -> play
	// transitions; _resumeGraceUntil ends the post-resume grace window.
	private int _endSignalStreak;
	private string? _prevState;
	private DateTimeOffset _resumeGraceUntil = DateTimeOffset.MinValue;


	public RokuMonitor(Store store, RokuClient roku)
	{
		_store = store;
		_roku = roku;
	}

	public void Start()
	{
		if (_cts != null) return;
		_cts = new CancellationTokenSource();
		_ = Loop(_cts.Token);
	}

	private async Task Loop(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			try
			{
				if ((App.InForeground || App.MonitorServiceActive) && _store.Loaded)
				{
					var status = await _roku.GetMediaPlayerAsync();
					Last = status;
					LastPollAt = DateTimeOffset.Now;
					Evaluate(status);
					StatusChanged?.Invoke();
				}
			}
			catch { /* a failed poll just waits for the next one */ }

			// Adaptive cadence (iteration 5) — how OFTEN we look, nothing
			// else: while Prime is playing, poll every second so the
			// hero's position/progress/button move in near real time and
			// completion is caught promptly for autoplay; paused gets
			// 2 s; Prime open but idle (menus / page / stopped) 5 s;
			// anything else keeps the slower configured rate. The
			// completion-detection thresholds above are unchanged.
			var delay = Last switch
			{
				{ IsPrime: true, State: "play" } => 1,
				{ IsPrime: true, State: "pause" } => 2,
				{ IsPrime: true } => 5,
				_ => Math.Max(5, _store.PollSeconds),
			};
			try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
			catch (TaskCanceledException) { break; }
		}
	}


	private void Evaluate(MediaPlayerStatus s)
	{
		var next = _store.UpNext;
		if (next == null) return;

		if (_trackedKey != next.Key)
		{
			_trackedKey = next.Key;
			_sawAdvancing = false;
			_lastPosition = 0;
			_maxPosition = 0;
			_endSignalStreak = 0;
			_prevState = null;
			_resumeGraceUntil = DateTimeOffset.MinValue;
			// _disarmed intentionally persists across Up Next changes:
			// re-arming requires an observed position reset (below).
		}

		// While the episode the app last started is the one being tracked,
		// keep its last-seen position fresh for GuidedPlayer's
		// already-playing guard.
		if (_store.LastStartedEpisodeKey == next.Key && s.IsPrime && s.PositionMs is long livePos)
			_store.NoteLastStartedPosition(livePos);

		long runtimeMs = (long)(next.RuntimeMinutes ?? 0) * 60_000;
		long pos = s.PositionMs ?? 0;

		// Credits-threshold setting, read as a parameter (see class
		// doc). +Infinity for "credits" makes Trigger A's comparison
		// simply never true — the detection flow is otherwise identical.
		double triggerAFraction = _store.CreditsThreshold switch
		{
			Store.CreditsEarly => 0.85,
			Store.CreditsLetPlay => double.PositiveInfinity,
			_ => 0.92,
		};

		// Iteration 7.1 — resume handling. An observed pause -> play
		// transition opens a ~4 s grace window: the media-player's
		// readings while the stream re-buffers (position near zero, a
		// brief "close") are the resume flicker that used to trip
		// Trigger B, so inside the window end signals neither fire nor
		// accumulate toward the sustained streak. The first playing
		// position after the transition also re-baselines the session
		// if it sits far below the high-water mark: that's a
		// restart-like reading (or Sam rewound and replayed), i.e. a
		// new session — the pre-pause maximum must stop serving as
		// "got well into it" evidence. A genuine resume reads within
		// a second or two of the paused position, nowhere near half
		// below it, so it never re-baselines.
		bool justResumed = _prevState == "pause" && s.IsPrime && s.IsPlaying;
		if (justResumed)
		{
			_resumeGraceUntil = DateTimeOffset.Now + TimeSpan.FromSeconds(4);
			_endSignalStreak = 0;
			if (s.PositionMs != null && _maxPosition > 0 && pos < _maxPosition / 2)
			{
				_maxPosition = pos;
				_lastPosition = pos;
			}
		}
		_prevState = s.State;
		bool inResumeGrace = DateTimeOffset.Now < _resumeGraceUntil;

		// A position back near the start re-arms auto check-off.
		if (s.IsPrime && s.PositionMs != null && pos < 120_000)
			_disarmed = false;

		if (_disarmed) return;
		if (runtimeMs <= 0) return;

		bool endedSignal =
			(s.IsPrime && (s.State == "stop" || s.State == "close")) ||
			// Prime stopped being the active app (and the TV is answering).
			(!s.IsPrime && s.Reachable && _maxPosition > 0) ||
			// Position dropped back under a minute (e.g. Prime auto-played
			// the next episode) after having been well into this one.
			(s.IsPrime && s.PositionMs != null && pos < 60_000 && _maxPosition > 0);

		// Iteration 7.1 — sustained acceptance (see class doc). The
		// three clauses above remain the DEFINITION of an end signal;
		// what changed is how much evidence Trigger B demands. Normal
		// playback (Prime playing, position >= 60 s) zeroes the streak
		// — the flicker shapes are always interrupted by a genuine
		// playing poll once the stream recovers. An end-signal poll
		// grows the streak, except inside the resume grace window,
		// where end signals neither fire nor accumulate. Any other
		// non-end-signal poll also breaks the run.
		if (s.IsPrime && s.IsPlaying && s.PositionMs != null && pos >= 60_000)
			_endSignalStreak = 0;
		else if (endedSignal && !inResumeGrace)
			_endSignalStreak++;
		else if (!endedSignal)
			_endSignalStreak = 0;

		if (s.IsPrime && s.PositionMs != null)
		{
			if (s.IsPlaying && pos > _lastPosition) _sawAdvancing = true;
			if (pos > _maxPosition) _maxPosition = pos;
			_lastPosition = pos;

			// Trigger A: ran (nearly) the whole listed runtime.
			if (_sawAdvancing && pos >= triggerAFraction * runtimeMs)
			{

				AutoCheck(next);
				return;
			}
		}

		// Trigger B: got well into the episode, then it ended / reset —
		// with the end signal SUSTAINED over two consecutive polls.
		// A genuine end presents exactly that (a settled close/stop, or
		// Prime's own advance holding position near zero across polls);
		// a one-poll resume flicker never reaches the threshold.
		if (_sawAdvancing && _maxPosition >= 0.65 * runtimeMs && _endSignalStreak >= 2)
		{

			AutoCheck(next);
		}
	}

	private void AutoCheck(Episode ep)
	{
		_disarmed = true;
		_sawAdvancing = false;
		_maxPosition = 0;
		_lastPosition = 0;
		_endSignalStreak = 0;
		_store.SetWatched(ep, true);

		MainThread.BeginInvokeOnMainThread(() => EpisodeAutoChecked?.Invoke(ep));
	}
}
