using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using OneChicago.Models;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>One row of the Watch Order list.</summary>
public class EpisodeRow
{
	public Episode Ep { get; }
	public bool IsCurrent { get; }

	public EpisodeRow(Episode ep, bool isCurrent)
	{
		Ep = ep;
		IsCurrent = isCurrent;
	}

	public string CheckText => Ep.Watched ? "✓" : "";
	public string Title => Ep.Title;

	/// <summary>The sub-line as spans, so show-colour themes (One
	/// Chicago) can colour the show name while the rest stays muted.
	/// In every other theme both spans are the muted colour, exactly
	/// as the single-colour line looked before.</summary>
	public FormattedString SubFormatted
	{
		get
		{
			var fs = new FormattedString();
			fs.Spans.Add(new Span { Text = Ep.DisplayShow, TextColor = ThemeService.ShowLabelColor(Ep) });
			fs.Spans.Add(new Span
			{
				Text = $" · {Ep.SeasonEpisode}" + (Ep.AirDateDisplay.Length > 0 ? $" · {Ep.AirDateDisplay}" : ""),
				TextColor = ThemeService.ColorOf("Text3"),
			});
			return fs;
		}
	}

	public bool HasEvent => Ep.IsEvent;
	public string EventPill => Ep.EventPill;
	public Color TitleColor => Ep.Watched ? ThemeService.ColorOf("WatchedText") : ThemeService.ColorOf("Text");
	public FontAttributes TitleAttributes => IsCurrent ? FontAttributes.Bold : FontAttributes.None;
	public Color RowBackground => IsCurrent ? ThemeService.ColorOf("CurrentBg") : ThemeService.ColorOf("PageBg");
}

/// <summary>
/// The app's single page (redesign): a big "One Chicago" title, the
/// WATCH NOW hero, UP NEXT (5 roomy
/// rows), and the full WATCH ORDER with checkmarks — watched dimmed,
/// current highlighted. Settings and episode details are pushed pages.
/// </summary>
public partial class TrackerPage : ContentPage
{
	private static Color C(string key) => ThemeService.ColorOf(key);

	private bool _subscribed;
	private bool _setupPrompted;
	private Episode? _lastAutoChecked;

	// Spoiler guard: which episode the hero is showing, and whether Sam
	// has tapped to reveal its description on this view.
	private string? _heroKey;
	private bool _heroDescRevealed;

	/// <summary>What the hero's action button currently does. Driven by
	/// the TV polling (see RefreshPlayButton): Sequence = run the guided
	/// Play sequence (the shipped behaviour); KeypressToggle = the TV
	/// already has this episode up playing or paused, so the button is
	/// a plain remote toggle — one Play keypress, nothing else.</summary>
	private enum HeroButtonMode { Sequence, KeypressToggle }
	private HeroButtonMode _heroMode = HeroButtonMode.Sequence;

	public TrackerPage()
	{
		InitializeComponent();
		// The hero description used to carry its own spoiler-reveal tap
		// recognizer. Since v1.4 the whole hero header is tappable (it
		// opens the episode details); OnHeroCardTapped keeps the reveal
		// behaviour as its first branch, so that recognizer is gone.
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await AppHost.Ready;

		if (!_subscribed)
		{
			_subscribed = true;
			AppHost.Store.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshAll);
			AppHost.Monitor.StatusChanged += () => MainThread.BeginInvokeOnMainThread(UpdateHeroLive);
			AppHost.Monitor.EpisodeAutoChecked += ep => MainThread.BeginInvokeOnMainThread(() =>
			{
				_lastAutoChecked = ep;
				AutoCheckText.Text = $"✓ Watched “{ep.Title}” — auto-checked from the TV.";
				AutoCheckBanner.IsVisible = true;
				RefreshAll();
			});
			AppHost.Player.RunStateChanged += () => MainThread.BeginInvokeOnMainThread(RefreshPlayButton);
			AppHost.Autoplay.StateChanged += () => MainThread.BeginInvokeOnMainThread(() =>
			{
				UpdateAutoplayUi();
				RefreshAll();
			});
			ThemeService.ThemeChanged += () => MainThread.BeginInvokeOnMainThread(() =>
			{
				ApplyThemeChrome();
				RefreshAll();
			});
		}

		ApplyThemeChrome();
		RefreshAll();

		// First-run setup (production Decision 7): a fresh install
		// (SetupCompleted false) is offered the setup flow once,
		// before anything else asks for attention. Existing installs
		// never see this — their progress file loads as completed.
		// Completing or skipping setup sets the flag, and Start over
		// is the only way back to false. The notifications prompt
		// waits for the next appearance, so the two never stack.
		if (!AppHost.Store.SetupCompleted && !_setupPrompted)
		{
			_setupPrompted = true;
			await Task.Delay(250);
			await Navigation.PushAsync(new SetupPage());
			return;
		}
		_ = MaybePromptForNotificationsAsync();
	}

	/// <summary>Iteration 6, once only: background mode's foreground
	/// service cannot exist without its visible notification, and
	/// Android 13+ makes notifications a permission. The first time
	/// this version runs with background mode on and the permission
	/// missing, explain briefly and fire the system request. Whatever
	/// Sam chooses is final — the marker is set before asking, this
	/// never nags twice, and Settings keeps showing the honest state
	/// (background mode unprotected) if he declines.</summary>
	private async Task MaybePromptForNotificationsAsync()
	{
		var store = AppHost.Store;
		if (!store.BackgroundModeEnabled || store.NotificationPromptDone) return;
#if ANDROID
		if (OneChicago.Platforms.MonitorServiceController.NotificationsAllowed())
		{
			store.MarkNotificationPromptDone();
			return;
		}
		store.MarkNotificationPromptDone();
		var allow = await DisplayAlert("Background mode needs notifications",
			"One Timeline can keep watching the TV and autoplaying while the app is closed. Android requires a small always-visible notification for that — it shows what's playing, with pause and next-episode buttons. Allow notifications to switch background mode fully on.",
			"Allow", "Not now");
		if (allow)
		{
			try { await Permissions.RequestAsync<Permissions.PostNotifications>(); }
			catch { /* the Settings page shows the resulting state either way */ }
		}
#endif
	}

	/// <summary>The parts of theming XAML resources can't reach:
	/// display font flavour on the big titles, and card shadows
	/// (dropped entirely by the flat/hairline Futuristic theme).</summary>
	private void ApplyThemeChrome()
	{
		var theme = ThemeService.Current;
		PageTitle.FontFamily = theme.DisplayFont;
		HeroTitle.FontFamily = theme.DisplayFont;
		Shadow? shadow = theme.FlatShadows
			? null
			: new Shadow { Brush = Colors.Black, Opacity = 0.07f, Radius = 12, Offset = new Point(0, 2) };
		HeroCard.Shadow = shadow;
		UpNextCard.Shadow = shadow;
	}

	private void RefreshAll()
	{
		var store = AppHost.Store;
		if (!store.Loaded) return;

		// The whole of Feature 2 (iteration 7): one line + one thin
		// bar. Sam rejected a stats page as bloat — nothing else.
		int watched = store.WatchedCount, total = store.Episodes.Count;
		int pct = total > 0 ? (int)Math.Round(100.0 * watched / total) : 0;
		ProgressLabel.Text = $"{watched} of {total} episodes · {pct}%";
		OverallProgress.Progress = total > 0 ? (double)watched / total : 0;
		RefreshHero();
		BuildUpNext();
		BuildRows();
		RefreshPlayButton();
		UpdateAutoplayUi();
	}

	// ---- Hero ----------------------------------------------------------

	private void RefreshHero()
	{
		var next = AppHost.Store.UpNext;
		if (next == null)
		{
			HeroShow.Text = "";
			HeroTitle.Text = "🎉 All caught up!";
			HeroMeta.Text = "";
			HeroEventPill.IsVisible = false;
			HeroEventLine.IsVisible = false;
			HeroDescription.Text = "Every episode in the watch order is checked off.";
			HeroDescCredit.IsVisible = false;
			HeroLiveLabel.IsVisible = false;
			HeroProgress.IsVisible = false;
			PlayNowButton.IsVisible = false;
			SkipButton.IsVisible = false;
			return;
		}

		PlayNowButton.IsVisible = true;
		HeroShow.Text = next.DisplayShow;
		HeroTitle.Text = next.Title;
		HeroMeta.Text = next.SeasonEpisode
			+ (next.AirDateDisplay.Length > 0 ? $" · {next.AirDateDisplay}" : "")
			+ (next.RuntimeMinutes is int r ? $" · {r} min" : "");
		HeroEventPill.IsVisible = next.IsEvent;
		HeroEventPillText.Text = next.EventPill;
		// The one slim crossover line (iteration 7): present only
		// when the current episode is part of an event, nothing else.
		var eventLine = EventLineText(next);
		HeroEventLine.IsVisible = eventLine.Length > 0;
		HeroEventLine.Text = eventLine;
		if (_heroKey != next.Key)
		{
			_heroKey = next.Key;
			_heroDescRevealed = false;
		}
		// Spoiler guard (default OFF = description always shown, exactly
		// as before): when ON, an unwatched episode's description stays
		// hidden until Sam taps it. The hero episode is by definition
		// unwatched.
		HeroDescription.Text = AppHost.Store.SpoilerGuardEnabled && !_heroDescRevealed
			? "Description hidden until watched — tap to reveal"
			: next.Description ?? "";
		// TVmaze attribution (CC BY-SA) sits with the text it covers:
		// shown exactly when a real description is on screen.
		HeroDescCredit.IsVisible = !string.IsNullOrWhiteSpace(next.Description)
			&& !(AppHost.Store.SpoilerGuardEnabled && !_heroDescRevealed);
		UpdateHeroLive();
	}

	/// <summary>Short show names for the crossover line — the compact
	/// forms Sam sees in the watch order ("P.D.", "SVU"), not the
	/// spelled-out display names.</summary>
	private static string ShortShowName(string show) => show switch
	{
		"PD" => "P.D.",
		"Law & Order: SVU" => "SVU",
		_ => show,
	};

	/// <summary>The hero's one slim event line (iteration 7):
	/// "Crossover event · Fire → Med → P.D." for a multi-show event
	/// (the event group's shows in watch order), "3-part story" for
	/// a same-show multi-part run, "" when the episode isn't in an
	/// event — in which case the line doesn't exist at all.</summary>
	private string EventLineText(Episode ep)
	{
		if (!ep.IsEvent) return "";
		var group = AppHost.Store.EventGroupOf(ep);
		if (group.Count < 2) return "";
		var shows = group.Select(e => ShortShowName(e.Show)).Distinct().ToList();
		return shows.Count > 1
			? $"Crossover event · {string.Join(" → ", shows)}"
			: $"{group.Count}-part story";
	}

	private void UpdateHeroLive()
	{
		var s = AppHost.Monitor.Last;
		var next = AppHost.Store.UpNext;

		// The status line agrees with the hero button (v1.4): Playing /
		// Paused at the TV's live position, or "Continue at …" when
		// Prime is open with nothing loaded but the app holds a saved
		// mid-episode position for the current episode.
		string? label = null;
		long? barPos = null;
		if (s is { IsPrime: true } && s.PositionMs is long pos)
		{
			if (s.State == "play")
			{
				label = $"Playing · {GuidedPlayer.FormatPosition(pos)}";
				barPos = pos;
			}
			else if (s.State == "pause")
			{
				label = $"Paused · {GuidedPlayer.FormatPosition(pos)}";
				barPos = pos;
			}
		}
		if (label == null && next != null && s is { IsPrime: true }
			&& s.State != "play" && s.State != "pause"
			&& AppHost.Store.LastStartedEpisodeKey == next.Key
			&& AppHost.Store.LastStartedPositionMs > 30_000)
		{
			label = $"Continue at {GuidedPlayer.FormatPosition(AppHost.Store.LastStartedPositionMs)}";
			barPos = AppHost.Store.LastStartedPositionMs;
		}

		if (label != null)
		{
			HeroLiveLabel.IsVisible = true;
			HeroLiveLabel.Text = label;
			if (barPos is long bp && next?.RuntimeMinutes is int rt && rt > 0)
			{
				HeroProgress.IsVisible = true;
				HeroProgress.Progress = Math.Min(1.0, (double)bp / ((long)rt * 60_000));
			}
			else
			{
				HeroProgress.IsVisible = false;
			}
		}
		else
		{
			HeroLiveLabel.IsVisible = false;
			HeroProgress.IsVisible = false;
		}

		RefreshPlayButton();
	}

	/// <summary>Hero card tap (v1.4): the card's title/meta/description
	/// area (HeroHeader — the action buttons live outside it) opens the
	/// current episode's detail page, exactly like tapping any other
	/// episode row. One preserved behaviour: while the spoiler guard is
	/// hiding the hero description, the first tap only reveals it (the
	/// old description-tap behaviour); once revealed, or with the guard
	/// off, the tap navigates.</summary>
	private async void OnHeroCardTapped(object sender, TappedEventArgs e)
	{
		var next = AppHost.Store.UpNext;
		if (next == null) return;

		if (AppHost.Store.SpoilerGuardEnabled && !_heroDescRevealed)
		{
			_heroDescRevealed = true;
			RefreshHero();
			return;
		}

		await Navigation.PushAsync(new EpisodeDetailPage(next));
	}

	// ---- Up Next ---------------------------------------------------------

	private void BuildUpNext()
	{
		UpNextList.Clear();
		var upcoming = AppHost.Store.Upcoming(5);
		if (upcoming.Count == 0)
		{
			UpNextList.Add(new Label
			{
				Text = "Nothing after this one.",
				FontSize = 15,
				TextColor = C("Text2"),
				Padding = new Thickness(16, 14),
			});
			return;
		}

		for (int i = 0; i < upcoming.Count; i++)
		{
			var ep = upcoming[i];
			var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(16, 14) };
			stack.Add(new Label
			{
				Text = ep.DisplayShow.ToUpperInvariant(),
				FontSize = 11,
				FontAttributes = FontAttributes.Bold,
				TextColor = ThemeService.ShowLabelColor(ep),
				CharacterSpacing = 0.8,
			});
			stack.Add(new Label { Text = ep.Title, FontSize = 17, TextColor = C("Text") });
			stack.Add(new Label
			{
				Text = ep.SeasonEpisode + (ep.AirDateDisplay.Length > 0 ? $" · {ep.AirDateDisplay}" : ""),
				FontSize = 13,
				TextColor = C("Text3"),
			});
			if (ep.IsEvent)
			{
				stack.Add(new Border
				{
					BackgroundColor = C("PillBg"),
					StrokeThickness = 0,
					Padding = new Thickness(8, 3),
					Margin = new Thickness(0, 4, 0, 0),
					HorizontalOptions = LayoutOptions.Start,
					StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8) },
					Content = new Label
					{
						Text = ep.EventPill,
						FontSize = 12,
						FontAttributes = FontAttributes.Bold,
						TextColor = C("PillText"),
					},
				});
			}

			var container = new VerticalStackLayout();
			container.Add(stack);
			if (i < upcoming.Count - 1)
				container.Add(new BoxView { HeightRequest = 0.5, BackgroundColor = C("Hairline"), Margin = new Thickness(16, 0, 0, 0) });
			container.GestureRecognizers.Add(new TapGestureRecognizer
			{
				Command = new Command(async () => await Navigation.PushAsync(new EpisodeDetailPage(ep))),
			});
			UpNextList.Add(container);
		}
	}

	// ---- Watch Order -----------------------------------------------------

	private void BuildRows()
	{
		var store = AppHost.Store;
		if (!store.Loaded) return;

		var q = (SearchEntry.Text ?? "").Trim().ToLowerInvariant();
		var next = store.UpNext;
		EpisodeList.ItemsSource = store.Episodes
			.Where(e => q.Length == 0
				|| $"{e.Title} {e.Code} {e.Show} {e.DisplayShow} {e.Order}".ToLowerInvariant().Contains(q))
			.Select(e => new EpisodeRow(e, next != null && e.Key == next.Key))
			.ToList();
	}

	private void OnSearchChanged(object sender, TextChangedEventArgs e) => BuildRows();

	private async void OnRowTapped(object sender, TappedEventArgs e)
	{
		if (sender is View v && v.BindingContext is EpisodeRow row)
			await Navigation.PushAsync(new EpisodeDetailPage(row.Ep));
	}

	// ---- Play ------------------------------------------------------------

	/// <summary>
	/// The hero button follows the TV (Sam, v1.4). Priority: a running
	/// sequence owns the button ("Working…"); an autoplay countdown
	/// owns the hero (label stays Play Now, and a tap fires the pending
	/// episode immediately, as shipped). Otherwise the latest poll
	/// decides:
	///  - Prime playing (state "play", position reported) → "⏸ Pause",
	///    tap = one Play keypress. (Media-player reports no title, so
	///    identity comes from the same last-started correspondence the
	///    guided sequence's guard uses — but Pause is shown whenever
	///    Prime is playing, since pausing whatever Prime is playing is
	///    what Sam asked for; the stricter advancing test stays inside
	///    GuidedPlayer, untouched.)
	///  - Prime paused at a real mid-episode position (> 30 s) →
	///    "▶ Continue Watching", tap = one Play keypress to resume.
	///  - Prime open with nothing loaded, and a saved mid-episode
	///    position for the current episode → "▶ Continue Watching",
	///    tap = the normal guided sequence (Prime resumes from its own
	///    saved progress via the episode page's Resume default).
	///  - Anything else → "▶ Play Now", the guided sequence, as shipped.
	/// Recomputed on every poll (via UpdateHeroLive), every store
	/// change and every player/autoplay state change, so the button
	/// flips on its own as the TV's state changes.
	/// </summary>
	private void RefreshPlayButton()
	{
		// First, so every return path below leaves it correct.
		UpdateSkipButton();
		if (AppHost.Player.IsRunning)
		{
			_heroMode = HeroButtonMode.Sequence;
			PlayNowButton.IsEnabled = false;
			PlayNowButton.Text = "Working…";
			return;
		}
		PlayNowButton.IsEnabled = true;

		if (AppHost.Autoplay.Phase == AutoplayPhase.CountingDown)
		{
			_heroMode = HeroButtonMode.Sequence;
			PlayNowButton.Text = "▶ Play Now";
			return;
		}

		var s = AppHost.Monitor.Last;
		var next = AppHost.Store.UpNext;
		if (s is { IsPrime: true } && s.PositionMs is long pos)
		{
			if (s.State == "play")
			{
				_heroMode = HeroButtonMode.KeypressToggle;
				PlayNowButton.Text = "⏸ Pause";
				return;
			}
			if (s.State == "pause" && pos > 30_000)
			{
				_heroMode = HeroButtonMode.KeypressToggle;
				PlayNowButton.Text = "▶ Continue Watching";
				return;
			}
		}
		if (next != null && s is { IsPrime: true }
			&& s.State != "play" && s.State != "pause"
			&& AppHost.Store.LastStartedEpisodeKey == next.Key
			&& AppHost.Store.LastStartedPositionMs > 30_000)
		{
			_heroMode = HeroButtonMode.Sequence;
			PlayNowButton.Text = "▶ Continue Watching";
			return;
		}

		_heroMode = HeroButtonMode.Sequence;
		PlayNowButton.Text = "▶ Play Now";
	}

	/// <summary>The skip button's two faces (iteration 7): while the
	/// TV has Prime up playing or paused, it's "Next episode ⏭";
	/// when the current episode has no Prime link — a Play button
	/// could only fail — it becomes the escape hatch, "Skip this
	/// episode". Otherwise it isn't shown at all. Never while a
	/// guided sequence is running (a skip mid-sequence could only be
	/// dropped as Busy anyway).</summary>
	private void UpdateSkipButton()
	{
		var next = AppHost.Store.UpNext;
		if (next == null || AppHost.Player.IsRunning)
		{
			SkipButton.IsVisible = false;
			return;
		}
		bool noLink = string.IsNullOrWhiteSpace(AppHost.Store.PrimeIdFor(next));
		var s = AppHost.Monitor.Last;
		bool tvActive = s is { IsPrime: true } && (s.State == "play" || s.State == "pause");
		SkipButton.IsVisible = noLink || tvActive;
		SkipButton.Text = noLink ? "Skip this episode" : "Next episode ⏭";
	}

	private async void OnPlayNow(object sender, EventArgs e)
	{
		// State-aware button (v1.4): when the TV already has an episode
		// up — playing, or paused mid-episode — the button is a plain
		// remote toggle. One Play keypress (Roku's play/pause toggle)
		// and nothing else: no sequence, no link, so a resume point can
		// never be disturbed. The next poll flips the button back on
		// its own (Pause ↔ Continue Watching).
		if (_heroMode == HeroButtonMode.KeypressToggle && !AppHost.Player.IsRunning)
		{
			await AppHost.Roku.SendKeyAsync("Play");
			return;
		}

		var next = AppHost.Store.UpNext;
		if (next == null) return;

		// If autoplay is counting down to this very episode, Play means
		// "don't wait" — fire it now instead of starting a second run.
		var ap = AppHost.Autoplay;
		if (ap.Phase == AutoplayPhase.CountingDown && ap.PendingEpisode?.Key == next.Key)
		{
			ap.PlayImmediately();
			return;
		}

		// A fresh manual Play starts a new autoplay session (clears a
		// reached session limit and its counter).
		ap.NoteManualPlay();

		HeroVerdict.IsVisible = false;

		try
		{
			var result = await AppHost.Player.PlayAsync(next, AppHost.Store.PrimeIdFor(next), next.Headline);
			HeroVerdict.IsVisible = true;
			if (result.Busy)
			{
				HeroVerdict.Text = "Already trying — that tap was ignored.";
				HeroVerdict.TextColor = C("VerdictWarn");
			}
			else if (result.AlreadyPlaying)
			{
				HeroVerdict.Text = $"Already playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)}. Nothing was sent.";
				HeroVerdict.TextColor = C("VerdictOk");
			}
			else if (result.Playing)
			{
				HeroVerdict.Text = $"Playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)} on the TV.";
				HeroVerdict.TextColor = C("VerdictOk");
			}
			else
			{
				HeroVerdict.Text = "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
				HeroVerdict.TextColor = C("VerdictWarn");
			}
		}
		catch (Exception ex)
		{

			HeroVerdict.IsVisible = true;
			HeroVerdict.Text = "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
			HeroVerdict.TextColor = C("VerdictWarn");
		}
		RefreshAll();
	}

	/// <summary>Skip (iteration 7): check the current episode off,
	/// advance the order, and — when the TV is in a state to be
	/// driven — start the next episode through the same single-flight
	/// guided Play autoplay uses. A manual action throughout: a
	/// pending autoplay countdown is cancelled (this tap replaces
	/// it), the manual-play session accounting is honoured, and
	/// GuidedPlayer's quiet period can only veto a repeat start of an
	/// episode just started — never the fresh next episode this skip
	/// targets, which by definition hasn't been started. No
	/// confirmation: Start from here (rewind) undoes a skip in two
	/// taps. When nothing is playing and the current episode simply
	/// has no Prime link, the skip is the escape hatch — it only
	/// advances, and sends nothing.</summary>
	private async void OnSkipNext(object sender, EventArgs e)
	{
		var store = AppHost.Store;
		var current = store.UpNext;
		if (current == null || AppHost.Player.IsRunning) return;

		if (AppHost.Autoplay.Phase != AutoplayPhase.Idle)
			AppHost.Autoplay.Cancel();
		AppHost.Autoplay.NoteManualPlay();

		bool currentHasLink = !string.IsNullOrWhiteSpace(AppHost.Store.PrimeIdFor(current));
		var s = AppHost.Monitor.Last;
		bool tvActive = s is { IsPrime: true } && (s.State == "play" || s.State == "pause");

		store.SetWatched(current, true);
		HeroVerdict.IsVisible = false;
		RefreshAll();

		var next = store.UpNext;
		if (next == null)
		{
			HeroVerdict.IsVisible = true;
			HeroVerdict.Text = "That was the last one 🎉 — the whole watch order is done.";
			HeroVerdict.TextColor = C("VerdictOk");
			return;
		}
		if (!tvActive && !currentHasLink)
		{
			// Escape hatch only: nothing on the TV to drive.
			HeroVerdict.IsVisible = true;
			HeroVerdict.Text = $"Skipped — “{next.Title}” is up next.";
			HeroVerdict.TextColor = C("VerdictOk");
			return;
		}
		if (string.IsNullOrWhiteSpace(AppHost.Store.PrimeIdFor(next)))
		{
			HeroVerdict.IsVisible = true;
			HeroVerdict.Text = $"Skipped ✓. “{next.Title}” has no Prime link — open it on the TV manually, or skip it too.";
			HeroVerdict.TextColor = C("VerdictOk");
			return;
		}

		try
		{
			var result = await AppHost.Player.PlayAsync(next, AppHost.Store.PrimeIdFor(next), next.Headline);
			HeroVerdict.IsVisible = true;
			if (result.Busy)
			{
				HeroVerdict.Text = "Skipped ✓ — a play sequence was already running, so the next episode wasn't started.";
				HeroVerdict.TextColor = C("VerdictWarn");
			}
			else if (result.AlreadyPlaying || result.Playing)
			{
				HeroVerdict.Text = $"Skipped ✓ — now playing “{next.Title}” at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)}.";
				HeroVerdict.TextColor = C("VerdictOk");
			}
			else
			{
				HeroVerdict.Text = $"Skipped ✓ — “{next.Title}” didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
				HeroVerdict.TextColor = C("VerdictWarn");
			}
		}
		catch (Exception ex)
		{

			HeroVerdict.IsVisible = true;
			HeroVerdict.Text = $"Skipped ✓ — “{next.Title}” didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
			HeroVerdict.TextColor = C("VerdictWarn");
		}
		RefreshAll();
	}

	// ---- Autoplay --------------------------------------------------------

	private void UpdateAutoplayUi()
	{
		var ap = AppHost.Autoplay;
		switch (ap.Phase)
		{
			case AutoplayPhase.CountingDown when ap.PendingEpisode is { } p:
				HeroAutoplayLabel.IsVisible = true;
				HeroAutoplayLabel.Text = $"Next: “{p.Title}” — playing in {ap.SecondsLeft} s";
				CancelAutoplayButton.Text = "Cancel";
				CancelAutoplayButton.IsVisible = true;
				break;
			case AutoplayPhase.NoPrimeLink:
				HeroAutoplayLabel.IsVisible = true;
				HeroAutoplayLabel.Text = "No Prime link for this one — open it manually";
				CancelAutoplayButton.Text = "Dismiss";
				CancelAutoplayButton.IsVisible = true;
				break;
			case AutoplayPhase.SessionLimitReached:
				HeroAutoplayLabel.IsVisible = true;
				HeroAutoplayLabel.Text = "Autoplay paused — session limit reached";
				CancelAutoplayButton.Text = "Dismiss";
				CancelAutoplayButton.IsVisible = true;
				break;
			default:
				HeroAutoplayLabel.IsVisible = false;
				CancelAutoplayButton.IsVisible = false;
				break;
		}
	}

	private void OnCancelAutoplay(object sender, EventArgs e) => AppHost.Autoplay.Cancel();

	// ---- Misc --------------------------------------------------------------

	private async void OnOpenSettings(object sender, EventArgs e)
		=> await Navigation.PushAsync(new SettingsPage());

	private void OnUndoAutoCheck(object sender, EventArgs e)
	{
		if (_lastAutoChecked != null)
		{
			AppHost.Store.SetWatched(_lastAutoChecked, false);
			_lastAutoChecked = null;
		}
		AutoCheckBanner.IsVisible = false;
		RefreshAll();
	}
}
