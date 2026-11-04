using Android.App;
using Android.Content;
using Android.OS;
using OneChicago.Models;
using OneChicago.Services;

namespace OneChicago.Platforms;

/// <summary>
/// Start/stop + platform-status helpers for the background-mode
/// service (iteration 6). All entry points are safe to call when the
/// service isn't running, and EnsureStarted is idempotent — App calls
/// it on every launch/resume. Starts happen only from those foreground
/// contexts, as Android's background-start restrictions require.
/// </summary>
public static class MonitorServiceController
{
	public static void EnsureStarted()
	{
		// If the store has already loaded and Sam turned background
		// mode off, don't start at all (the service would only have to
		// stop itself again). Before first load we can't know yet —
		// the service checks the toggle itself once the store is in.
		try
		{
			if (AppHost.Store.Loaded && !AppHost.Store.BackgroundModeEnabled) return;
			var ctx = Android.App.Application.Context;
			var intent = new Intent(ctx, typeof(MonitorService));
			intent.SetAction(MonitorService.ActionStart);
			ctx.StartForegroundService(intent);
		}
		catch
		{
			// A refused start (platform timing, OEM quirks) must never
			// disturb the in-app experience — foreground use is
			// completely unaffected either way.
		}
	}

	public static void StopService()
	{
		try
		{
			var ctx = Android.App.Application.Context;
			ctx.StopService(new Intent(ctx, typeof(MonitorService)));
		}
		catch { /* not running — nothing to stop */ }
	}

	/// <summary>POST_NOTIFICATIONS state (Android 13+). Before that,
	/// notifications are simply on.</summary>
	public static bool NotificationsAllowed()
	{
		if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return true;
		try
		{
			return Android.App.Application.Context.CheckSelfPermission("android.permission.POST_NOTIFICATIONS")
				== Android.Content.PM.Permission.Granted;
		}
		catch { return false; }
	}

	/// <summary>Open this app's page in the system Settings app.
	/// Production (Decision 5) makes no direct battery-exemption
	/// request and holds no battery permission: the Settings page
	/// explains in plain text that background watching works best
	/// with the app's Battery setting on "Unrestricted", and this
	/// button takes the user to the system page where that setting
	/// (and the notifications toggle) lives.</summary>
	public static void OpenAppSettings()
	{
		try
		{
			var ctx = Android.App.Application.Context;
			var intent = new Intent(
				Android.Provider.Settings.ActionApplicationDetailsSettings,
				Android.Net.Uri.Parse("package:" + ctx.PackageName));
			intent.AddFlags(ActivityFlags.NewTask);
			ctx.StartActivity(intent);
		}
		catch { /* an OEM quirk here must never disturb the Settings page */ }
	}
}

/// <summary>
/// Background mode (iteration 6): an Android foreground service that
/// keeps the app's EXISTING singletons — the one Store, RokuMonitor,
/// AutoplayCoordinator and GuidedPlayer wired by AppHost — running
/// while the app isn't on screen. It creates no second monitor or
/// coordinator (that would reintroduce the double-firing the app
/// already fixed once); it only changes where the monitoring loop's
/// lifecycle lives. RokuMonitor polls whenever the app is in the
/// foreground OR App.MonitorServiceActive is set, which this service
/// sets for its whole lifetime.
///
/// Lifecycle: started from foreground contexts (app launch/resume,
/// the Settings toggle) when the persisted Background mode setting is
/// on; survives the UI being backgrounded and swiped from recents
/// (stopWithTask=false); sticky if the system kills it. It stops only
/// via the notification's Stop action, the Settings toggle, or a
/// force-stop. No boot start — Sam opens the app once.
///
/// The persistent notification is the "notification bar widget":
/// current episode + live playback state, with Pause/Resume, Next
/// episode (or Cancel during an autoplay countdown) and Stop actions.
/// The notification re-posts once a second so the position text moves
/// by the second; discrete state changes (play/pause, episode,
/// countdown) also update immediately.
/// </summary>
// stopWithTask="false" is declared on the service element in
// AndroidManifest.xml (this binding set has no such attribute
// property for services; the manifest is authoritative anyway).
[Service(Name = "com.shatterpro0f.onechicago.MonitorService", Exported = false)]
public class MonitorService : Service
{
	internal const string ActionStart = "com.shatterpro0f.onechicago.monitor.START";
	internal const string ActionTogglePlay = "com.shatterpro0f.onechicago.monitor.TOGGLE_PLAY";
	internal const string ActionNext = "com.shatterpro0f.onechicago.monitor.NEXT";
	internal const string ActionCancelCountdown = "com.shatterpro0f.onechicago.monitor.CANCEL_COUNTDOWN";
	internal const string ActionStopService = "com.shatterpro0f.onechicago.monitor.STOP";

	private const int NotificationId = 1001;
	private const string ChannelId = "onechicago-monitor";

	private bool _foregroundStarted;
	private bool _initStarted;
	private CancellationTokenSource? _tickCts;

	private readonly object _updateLock = new();
	private DateTimeOffset _lastNotifyAt = DateTimeOffset.MinValue;
	private string _lastSignature = "";

	public override IBinder? OnBind(Intent? intent) => null;

	public override void OnCreate()
	{
		base.OnCreate();
	}

	/// <summary>System timeout callback for a foreground service
	/// (the callback itself only exists on Android 34+, and the
	/// platform only ever delivers it there). The base implementation
	/// is expected to stop the service.</summary>
	[System.Runtime.Versioning.SupportedOSPlatform("android34.0")]
	public override void OnTimeout(int startId)
	{
		base.OnTimeout(startId);
	}

	public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
	{
		switch (intent?.Action)
		{
			case ActionStopService:

				StopMonitorService();
				return StartCommandResult.NotSticky;
			case ActionTogglePlay:

				_ = TogglePlayAsync();
				break;
			case ActionNext:

				_ = NextEpisodeAsync();
				break;
			case ActionCancelCountdown:

				try { AppHost.Autoplay.Cancel(); } catch { /* not counting down */ }
				RequestUpdate();
				break;
		}

		// Start, system restart (null intent), and every action path
		// land here: the service must be in the foreground state
		// before anything else (Android gives a started foreground
		// service only a few seconds to call StartForeground).
		EnsureForeground();
		_ = InitAsync();
		return StartCommandResult.Sticky;
	}

	/// <summary>Swiping the app from recents must NOT stop background
	/// mode — Sam's whole point is that it keeps running. Deliberately
	/// does nothing.</summary>
	public override void OnTaskRemoved(Intent? rootIntent) { }

	public override void OnDestroy()
	{
		App.MonitorServiceActive = false;
		_tickCts?.Cancel();
		if (_initStarted)
		{
			try
			{
				AppHost.Monitor.StatusChanged -= OnAnyStateChanged;
				AppHost.Autoplay.StateChanged -= OnAnyStateChanged;
				AppHost.Store.Changed -= OnAnyStateChanged;
				AppHost.Player.RunStateChanged -= OnAnyStateChanged;
			}
			catch { /* process may be half-torn-down already */ }
		}
		base.OnDestroy();
	}

	private void EnsureForeground()
	{
		CreateChannel();
		var notification = BuildNotification(Render());
		// The two-argument overload: the foreground-service type
		// (connectedDevice) comes from the manifest declaration,
		// which is also where its type-specific permission lives.
		StartForeground(NotificationId, notification);
		_foregroundStarted = true;
		App.MonitorServiceActive = true;
	}

	private async Task InitAsync()
	{
		if (_initStarted) { RequestUpdate(); return; }
		_initStarted = true;
		try { await AppHost.Ready; } catch { /* store failed to load; the plain notification still shows */ }

		// Honour the toggle: if Sam turned background mode off while
		// this start was in flight, stop again right away.
		if (AppHost.Store.Loaded && !AppHost.Store.BackgroundModeEnabled)
		{
			StopMonitorService();
			return;
		}

		AppHost.Monitor.StatusChanged += OnAnyStateChanged;
		AppHost.Autoplay.StateChanged += OnAnyStateChanged;
		AppHost.Store.Changed += OnAnyStateChanged;
		AppHost.Player.RunStateChanged += OnAnyStateChanged;

		_tickCts = new CancellationTokenSource();
		_ = TickLoop(_tickCts.Token);
		RequestUpdate();
	}

	private async Task TickLoop(CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			RequestUpdate();
			try { await Task.Delay(1000, ct); }
			catch (TaskCanceledException) { break; }
		}
	}

	private void OnAnyStateChanged()
	{
		RequestUpdate();
	}

	private void StopMonitorService()
	{
		App.MonitorServiceActive = false;
		_tickCts?.Cancel();
		if (_foregroundStarted)
		{
			try { StopForeground(StopForegroundFlags.Remove); } catch { /* already down */ }
			_foregroundStarted = false;
		}
		StopSelf();
	}

	// ---- Notification actions ----------------------------------------

	/// <summary>The same single Play keypress the hero button sends —
	/// Roku's play/pause toggle. Nothing else, no sequence.</summary>
	private async Task TogglePlayAsync()
	{
		try { await AppHost.Roku.SendKeyAsync("Play"); } catch { /* TV unreachable: no-op */ }
		RequestUpdate();
	}

	/// <summary>"Next episode": check off the current episode and run
	/// the guided Play for the next one in the watch order — the same
	/// pieces autoplay's jump uses (SetWatched + the single-flight
	/// GuidedPlayer), minus the countdown. A pending countdown is
	/// cancelled first so the two can never race, and the manual-play
	/// session accounting is honoured like any manual Play.</summary>
	private async Task NextEpisodeAsync()
	{
		var store = AppHost.Store;
		var player = AppHost.Player;
		if (!store.Loaded)
		{

			return;
		}
		if (player.IsRunning)
		{

			return;
		}

		if (AppHost.Autoplay.Phase == AutoplayPhase.CountingDown)
			AppHost.Autoplay.Cancel();
		AppHost.Autoplay.NoteManualPlay();

		var current = store.UpNext;
		if (current == null)
		{

			RequestUpdate();
			return;
		}
		store.SetWatched(current, true);

		var next = store.UpNext;
		if (next == null)
		{

			RequestUpdate();
			return;
		}
		if (string.IsNullOrWhiteSpace(store.PrimeIdFor(next)))
		{
			// Mirrors autoplay's no-link behaviour: checked off,
			// advanced, nothing sent — the notification now shows the
			// new current episode, which is the honest state.

			RequestUpdate();
			return;
		}

		await player.PlayAsync(next, store.PrimeIdFor(next), next.Headline);
		RequestUpdate();
	}

	// ---- Notification rendering --------------------------------------

	private record RenderState(string Title, string Text, string ToggleLabel, int ToggleIcon, bool CountingDown, string Signature);

	private RenderState Render()
	{
		var store = AppHost.Store;
		var autoplay = AppHost.Autoplay;
		var status = AppHost.Monitor.Last;

		// Autoplay countdown owns the notification while it runs.
		if (autoplay.Phase == AutoplayPhase.CountingDown && autoplay.PendingEpisode is { } pending)
		{
			return new RenderState(
				pending.Headline,
				$"Next: starts in {Clock(autoplay.SecondsLeft * 1000L)} — tap Cancel to stop this jump",
				"Play", Android.Resource.Drawable.IcMediaPlay, true,
				$"countdown|{pending.Key}|{autoplay.SecondsLeft}");
		}

		if (!store.Loaded)
		{
			return new RenderState(
				"One Timeline", "Loading your watch order…",
				"Play", Android.Resource.Drawable.IcMediaPlay, false, "loading");
		}

		var ep = store.UpNext;
		if (ep == null)
		{
			return new RenderState(
				"One Timeline", "Every episode watched 🎉",
				"Play", Android.Resource.Drawable.IcMediaPlay, false, "alldone");
		}

		string total = ep.RuntimeMinutes is int r ? $"{r}:00" : "";
		string OfTotal(long pos) => total.Length > 0 ? $"{Clock(pos)} of {total}" : Clock(pos);

		string text, toggleLabel;
		int toggleIcon;
		string mode;
		if (status is { IsPrime: true, State: "play", PositionMs: long pos })
		{
			text = $"Playing · {OfTotal(pos)}";
			toggleLabel = "Pause";
			toggleIcon = Android.Resource.Drawable.IcMediaPause;
			mode = "playing";
		}
		else if (status is { IsPrime: true, State: "pause", PositionMs: long ppos })
		{
			text = $"Paused · {OfTotal(ppos)}";
			toggleLabel = "Resume";
			toggleIcon = Android.Resource.Drawable.IcMediaPlay;
			mode = "paused";
		}
		else if (status is { IsPrime: true })
		{
			// Prime open in menus / on a page. If the app has a saved
			// mid-episode position for this episode, say where it
			// would continue — the same reading the hero uses.
			if (store.LastStartedEpisodeKey == ep.Key && store.LastStartedPositionMs > 30_000)
				text = $"Continue at {OfTotal(store.LastStartedPositionMs)}";
			else
				text = "Prime is open — nothing playing";
			toggleLabel = "Play";
			toggleIcon = Android.Resource.Drawable.IcMediaPlay;
			mode = "primeidle";
		}
		else if (status is { Reachable: true })
		{
			text = "TV is on — Prime isn't open";
			toggleLabel = "Play";
			toggleIcon = Android.Resource.Drawable.IcMediaPlay;
			mode = "tvon";
		}
		else
		{
			text = "Waiting for the TV…";
			toggleLabel = "Play";
			toggleIcon = Android.Resource.Drawable.IcMediaPlay;
			mode = "waiting";
		}

		return new RenderState(ep.Headline, text, toggleLabel, toggleIcon, false,
			$"{ep.Key}|{mode}|{toggleLabel}");
	}

	// Iteration 7 fix: the notification's subject episode is ALWAYS
	// Store.UpNext — it follows the watch order exactly like the main
	// screen. (v1.6 showed the episode matching LastStartedEpisodeKey
	// whenever Prime was open, so after an episode ended and was
	// auto-checked off, the notification kept displaying the finished
	// episode while the order had already moved on.) The last-started
	// record now feeds ONLY the "Continue at …" position text in
	// Render(), and only when it names the current episode — the
	// same reading the hero uses.

	private static string Clock(long ms)
	{
		var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
		return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
	}

	private Notification.Action MakeAction(int iconRes, string label, string action, int requestCode)
	{
		var icon = Android.Graphics.Drawables.Icon.CreateWithResource(this, iconRes);
		return new Notification.Action.Builder(icon, label, ActionPendingIntent(action, requestCode)).Build();
	}

	private Notification BuildNotification(RenderState r)
	{
		var style = new Notification.MediaStyle().SetShowActionsInCompactView(0, 1);

		var builder = new Notification.Builder(this, ChannelId)
			.SetContentTitle(r.Title)
			.SetContentText(r.Text)
			.SetSmallIcon(ApplicationInfo?.Icon ?? Android.Resource.Drawable.SymActionCall)
			.SetContentIntent(ContentPendingIntent())
			.SetOngoing(true)
			.SetOnlyAlertOnce(true)
			.SetShowWhen(false)
			.SetVisibility(NotificationVisibility.Public)
			.SetStyle(style)
			.AddAction(MakeAction(r.ToggleIcon, r.ToggleLabel, ActionTogglePlay, 1));

		if (r.CountingDown)
		{
			builder.AddAction(MakeAction(
				Android.Resource.Drawable.IcMenuCloseClearCancel, "Cancel",
				ActionCancelCountdown, 2));
		}
		else
		{
			builder.AddAction(MakeAction(
				Android.Resource.Drawable.IcMediaNext, "Next episode",
				ActionNext, 2));
		}

		builder.AddAction(MakeAction(
			Android.Resource.Drawable.IcLockPowerOff, "Stop",
			ActionStopService, 3));

		return builder.Build();
	}

	private PendingIntent ContentPendingIntent()
	{
		var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!)
			?? new Intent(this, typeof(MainActivity));
		launch.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
		return PendingIntent.GetActivity(this, 0, launch,
			PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
	}

	private PendingIntent ActionPendingIntent(string action, int requestCode)
	{
		var intent = new Intent(this, typeof(MonitorService));
		intent.SetAction(action);
		// Iteration 7.1: a unique data URI per action. PendingIntent
		// identity is decided by action + data + component (extras are
		// ignored), so intents that differ only by action string rest
		// on thinner ice than they should; the per-action URI — on top
		// of the distinct request codes — makes each notification
		// action's intent unmistakably its own, so the system can
		// never deliver one action's intent for another's tap.
		intent.SetData(Android.Net.Uri.Parse($"onechicago://monitor-action/{action}"));
		return PendingIntent.GetService(this, requestCode, intent,
			PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
	}

	/// <summary>Notification refresh: discrete state changes (a new
	/// render signature — play/pause, episode, countdown, and each
	/// countdown second) post immediately; otherwise the 1-second
	/// tick re-posts once a second so the position text moves by the
	/// second, as Sam asked (iteration 7 — the earlier ~5 s throttle
	/// made the position visibly lag). OnlyAlertOnce is set on the
	/// notification, so per-second re-posts never buzz. The 1 s floor
	/// also collapses the duplicate requests a single state change
	/// can raise through several events at once.</summary>
	private void RequestUpdate()
	{
		try
		{
			Notification notification;
			lock (_updateLock)
			{
				var r = Render();
				var now = DateTimeOffset.UtcNow;
				bool changed = r.Signature != _lastSignature;
				if (!changed && now - _lastNotifyAt < TimeSpan.FromSeconds(1)) return;
				_lastSignature = r.Signature;
				_lastNotifyAt = now;
				notification = BuildNotification(r);
			}
			var nm = (NotificationManager)GetSystemService(NotificationService)!;
			nm.Notify(NotificationId, notification);
		}
		catch { /* a failed render must never take the service down */ }
	}

	private void CreateChannel()
	{
		if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
		var nm = (NotificationManager)GetSystemService(NotificationService)!;
		if (nm.GetNotificationChannel(ChannelId) != null) return;
		var channel = new NotificationChannel(ChannelId, "Background watching", NotificationImportance.Low)
		{
			Description = "Shows what's playing while One Timeline keeps watching in the background",
		};
		nm.CreateNotificationChannel(channel);
	}
}
