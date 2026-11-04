using OneChicago.Models;

namespace OneChicago.Services;

public enum AutoplayPhase
{
	Idle,
	CountingDown,
	NoPrimeLink,
	SessionLimitReached,
}

/// <summary>
/// Autoplay: when RokuMonitor's completion detection auto-checks the
/// current episode (Prime playing, position ≥92% of the listed runtime,
/// or a clear end-of-playback after ≥65%), the episode is already marked
/// watched by the monitor — this coordinator handles what happens next:
///
///  - If autoplay is off, nothing.
///  - If the new current episode has no Prime ID (e.g. Chicago Justice
///    has no Prime listing in Canada), the hero shows a plain notice and
///    NO commands are fired.
///  - Otherwise a countdown runs on the hero ("Next: … — playing
///    in N s", with Cancel). Its length is Sam's Settings choice
///    (Store.AutoplayCountdownSeconds: free-form 0–600 s since
///    iteration 9, default 10; 0 = start immediately, which the
///    countdown loop below expresses as a single 1 s tick).
///    Cancel stops this one jump only. When the countdown expires,
///    the guided Play sequence runs for the next episode — through
///    the same single-flight GuidedPlayer as every manual Play
///    button, so autoplay can never stack sequences.
///
/// Runs only while the app is open: the monitor only polls in the
/// foreground, and the completion event is raised on the main thread.
///
/// Iteration 5 hardening (Sam's rollover restarted the new episode
/// once, untouched): completion handling is idempotent per episode
/// (see _handled), and neither a countdown nor a jump fires for an
/// episode inside GuidedPlayer's 60-second post-success quiet period.
/// </summary>
public class AutoplayCoordinator
{
	private readonly Store _store;
	private readonly GuidedPlayer _player;
	private CancellationTokenSource? _countdownCts;

	/// <summary>Automatic jumps actually fired since the app opened.
	/// Compared against Store.AutoplaySessionLimit (0 = no limit, the
	/// default — in which case this counter changes nothing). Manual
	/// Play taps never touch it; a fresh manual Play resets it via
	/// NoteManualPlay.</summary>
	private int _jumpsThisSession;

	/// <summary>Idempotent completion handling (iteration 5): episode
	/// keys this coordinator has already acted on, with when. A second
	/// completion event for the same episode inside the window is
	/// ignored completely — no second countdown, no second Play — so a
	/// duplicated or late event can never restart a rollover that
	/// already happened. The window (10 min) is far longer than any
	/// rollover takes and far shorter than watching the episode again
	/// (a rewind + full rewatch is 40+ min), so legitimate replays are
	/// unaffected. In memory only: it needs to survive the minutes
	/// around a rollover, not an app restart.</summary>
	private readonly Dictionary<string, DateTimeOffset> _handled = new();
	private static readonly TimeSpan HandledWindow = TimeSpan.FromMinutes(10);

	public AutoplayPhase Phase { get; private set; } = AutoplayPhase.Idle;
	public Episode? PendingEpisode { get; private set; }
	public int SecondsLeft { get; private set; }

	public event Action? StateChanged;

	public AutoplayCoordinator(Store store, RokuMonitor monitor, GuidedPlayer player)
	{
		_store = store;
		_player = player;
		monitor.EpisodeAutoChecked += OnEpisodeAutoChecked;
	}

	private void OnEpisodeAutoChecked(Episode finished)
	{
		// Idempotency first (iteration 5): a completion event for an
		// episode already acted on is ignored completely, whatever the
		// settings — a rollover must happen exactly once.
		if (_handled.TryGetValue(finished.Key, out var handledAt)
			&& DateTimeOffset.UtcNow - handledAt < HandledWindow)
		{

			return;
		}
		_handled[finished.Key] = DateTimeOffset.UtcNow;

		if (!_store.AutoplayEnabled)
		{

			return;
		}
		if (Phase == AutoplayPhase.CountingDown)
		{

			return;
		}

		var next = _store.UpNext;
		if (next == null)
		{

			return;
		}

		// Quiet period (iteration 5): if the guided player got this
		// next episode playing within the last 60 s (e.g. this event
		// arrived late, after a jump already fired), starting a
		// countdown for it now would only queue a phantom second start.
		if (_player.InQuietPeriod(next.Key))
		{

			return;
		}

		PendingEpisode = next;
		// Session limit (default: none — this branch never fires and
		// behaviour is exactly as shipped). The finished episode is
		// already checked off by the monitor either way; only the
		// countdown/jump is withheld, and the hero says why.
		if (_store.AutoplaySessionLimit > 0 && _jumpsThisSession >= _store.AutoplaySessionLimit)
		{

			Phase = AutoplayPhase.SessionLimitReached;
			StateChanged?.Invoke();
			return;
		}
		if (string.IsNullOrWhiteSpace(_store.PrimeIdFor(next)))
		{

			Phase = AutoplayPhase.NoPrimeLink;
			StateChanged?.Invoke();
			return;
		}
		_ = RunCountdownAsync(next);
	}

	private async Task RunCountdownAsync(Episode next)
	{
		_countdownCts?.Cancel();
		var cts = new CancellationTokenSource();
		_countdownCts = cts;

		Phase = AutoplayPhase.CountingDown;
		SecondsLeft = Math.Max(1, _store.AutoplayCountdownSeconds);
		StateChanged?.Invoke();

		try
		{
			while (SecondsLeft > 0)
			{
				await Task.Delay(1000, cts.Token);
				SecondsLeft--;
				StateChanged?.Invoke();
			}
		}
		catch (TaskCanceledException)
		{
			return; // Cancel() already reset the state.
		}

		Phase = AutoplayPhase.Idle;
		PendingEpisode = null;
		StateChanged?.Invoke();

		await FireAsync(next);
	}

	private async Task FireAsync(Episode next)
	{
		if (_player.InQuietPeriod(next.Key))
		{

			return;
		}
		if (_player.IsRunning)
		{

			return;
		}
		_jumpsThisSession++; // a jump that actually fires counts toward the session limit

		await _player.PlayAsync(next, _store.PrimeIdFor(next), next.Headline);
	}

	/// <summary>A manual Play Now tap starts a new autoplay session:
	/// the jump counter resets and a showing session-limit notice
	/// clears. Called by the manual Play entry points (never by the
	/// countdown path, whose jump counts like any other).</summary>
	public void NoteManualPlay()
	{
		_jumpsThisSession = 0;
		if (Phase == AutoplayPhase.SessionLimitReached)
		{
			Phase = AutoplayPhase.Idle;
			PendingEpisode = null;
			StateChanged?.Invoke();
		}
	}

	/// <summary>Stop the pending jump (this one only — autoplay stays on
	/// for future episodes). Also dismisses the no-link notice.</summary>
	public void Cancel()
	{
		if (Phase == AutoplayPhase.Idle) return;
		_countdownCts?.Cancel();
		_countdownCts = null;
		Phase = AutoplayPhase.Idle;
		PendingEpisode = null;
		StateChanged?.Invoke();
	}

	/// <summary>Skip the rest of the countdown and play right now
	/// (the hero's Play button does this while a countdown is up).</summary>
	public void PlayImmediately()
	{
		if (Phase != AutoplayPhase.CountingDown || PendingEpisode is not { } next) return;
		_countdownCts?.Cancel();
		_countdownCts = null;
		Phase = AutoplayPhase.Idle;
		PendingEpisode = null;
		StateChanged?.Invoke();
		_ = FireAsync(next);
	}
}
