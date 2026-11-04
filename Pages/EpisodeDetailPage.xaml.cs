using OneChicago.Models;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// Clean episode detail (pushed from the main page): title, meta,
/// description (TVmaze, credited on the page), cameos, crossover
/// context — and the actions: Play, Start from here, Mark watched.
///
/// Streaming links (Decision 1G): the packs ship WITH their
/// episode links. This page carries the "Your streaming links"
/// editor on top of them — one section per platform: the user
/// pastes a link they copied from that service themselves, the
/// Store validates it against the platform's ID form and keeps
/// it as their override (the top precedence layer), and it can
/// be removed again here. The badges under the meta line list
/// EVERY link the entry carries; the Play button appears only
/// when a link sits on a service in My Services — an entry
/// whose links are all elsewhere is tracker-only, exactly like
/// an episode with no links at all.
/// </summary>
public partial class EpisodeDetailPage : ContentPage
{
	private readonly Episode _ep;

	// Spoiler guard: whether Sam has tapped to reveal this episode's
	// description on this view.
	private bool _descRevealed;

	public EpisodeDetailPage(Episode ep)
	{
		InitializeComponent();
		_ep = ep;
		DescriptionLabel.GestureRecognizers.Add(new TapGestureRecognizer
		{
			Command = new Command(() =>
			{
				if (AppHost.Store.SpoilerGuardEnabled && !_ep.Watched && !_descRevealed)
				{
					_descRevealed = true;
					Bind();
				}
			}),
		});
		BuildLinkEditors();
		Bind();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		AppHost.Player.RunStateChanged += OnPlayRunStateChanged;
		RefreshPlayButton();
		Bind();
	}

	protected override void OnDisappearing()
	{
		AppHost.Player.RunStateChanged -= OnPlayRunStateChanged;
		base.OnDisappearing();
	}

	private void OnPlayRunStateChanged() => MainThread.BeginInvokeOnMainThread(RefreshPlayButton);

	private void RefreshPlayButton()
	{
		bool running = AppHost.Player.IsRunning;
		PlayEpisodeButton.IsEnabled = !running;
		PlayEpisodeButton.Text = running ? "Working…" : "▶ Play";
	}

	private void Bind()
	{
		Title = $"{_ep.DisplayShow} {_ep.Code}";
		ShowLabel.Text = _ep.DisplayShow.ToUpperInvariant();
		ShowLabel.TextColor = ThemeService.ShowLabelColor(_ep);
		HeadlineLabel.Text = _ep.Title;
		HeadlineLabel.FontFamily = ThemeService.Current.DisplayFont;
		MetaLabel.Text = _ep.SeasonEpisode
			+ (_ep.AirDateDisplay.Length > 0 ? $" · {_ep.AirDateDisplay}" : "")
			+ (_ep.RuntimeMinutes is int r ? $" · {r} min" : "")
			+ $" · #{_ep.Order} in the watch order";

		// Spoiler guard (default OFF = always shown, as shipped): ON
		// hides an unwatched episode's description until tapped.
		// Watched episodes always show theirs.
		bool descHidden = AppHost.Store.SpoilerGuardEnabled && !_ep.Watched && !_descRevealed;
		bool hasDesc = !string.IsNullOrWhiteSpace(_ep.Description);
		DescriptionLabel.Text = descHidden
			? "Description hidden until watched — tap to reveal"
			: hasDesc
				? _ep.Description
				: "(No description available for this episode.)";
		// TVmaze attribution (CC BY-SA) sits with the text it covers:
		// visible exactly when a real description is on screen.
		DescCreditLabel.IsVisible = hasDesc && !descHidden;

		// Badges: EVERY link the entry carries (dataset +
		// overrides), whatever My Services says — the detail page
		// shows what exists; playback rules live in Watch.
		var allLinks = AppHost.Store.LinksFor(_ep);
		LinksBadgeLabel.IsVisible = allLinks.Count > 0;
		LinksBadgeLabel.Text = allLinks.Count == 0
			? ""
			: "Plays on: " + string.Join(" · ",
				allLinks.Select(l => PlatformCatalog.Find(l.Platform)?.DisplayName ?? l.Platform));

		BindLinkEditors();
		// Play appears only when a link sits on a service the
		// user has (My Services): anything else is a checklist
		// entry and shows no Play button at all.
		PlayEpisodeButton.IsVisible = AppHost.Store.WatchLinksFor(_ep).Count > 0;

		if (_ep.Cameos.Count > 0)
		{
			CameosHeader.IsVisible = true;
			CameosLabel.IsVisible = true;
			CameosLabel.Text = string.Join("\n", _ep.Cameos.Select(c => $"• {c.Show}: {c.Names}"));
		}

		if (_ep.IsEvent)
		{
			EventPill.IsVisible = true;
			EventPillText.Text = _ep.EventPill;
			var group = AppHost.Store.EventGroupOf(_ep);
			EventGroupText.IsVisible = true;
			EventGroupText.Text = string.Join("\n", group.Select(e =>
				$"{(e.Key == _ep.Key ? "➤ " : "   ")}{e.Show} {e.Code} “{e.Title}”{(e.Watched ? " ✓ watched" : "")}"));
		}

		UpdateWatchedButton();
	}

	private void UpdateWatchedButton()
	{
		WatchedButton.Text = _ep.Watched ? "Mark unwatched" : "Mark watched";
	}

	// ---- Your streaming links -----------------------------------
	// One editor section per platform in the catalogue, built
	// ONCE in the constructor: a rebind (watched toggle, a save)
	// must never rebuild the sections and wipe an entry the user
	// is mid-paste in. Bind only refreshes each section's status
	// line and its Remove button's visibility.

	private sealed class LinkEditorRow
	{
		public required StreamingPlatform Platform;
		public required Label Status;
		public required Entry Entry;
		public required Button RemoveButton;
	}

	private readonly List<LinkEditorRow> _linkEditors = new();

	private void BuildLinkEditors()
	{
		foreach (var platform in PlatformCatalog.All)
		{
			var section = new VerticalStackLayout { Spacing = 0, Margin = new Thickness(0, 12, 0, 0) };

			var name = new Label
			{
				Text = platform.DisplayName.ToUpperInvariant(),
				FontSize = 13,
				FontAttributes = FontAttributes.Bold,
			};
			name.SetDynamicResource(Label.TextColorProperty, "ThText2");
			section.Add(name);

			var status = new Label { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap, Margin = new Thickness(0, 2, 0, 0) };
			status.SetDynamicResource(Label.TextColorProperty, "ThText2");
			section.Add(status);

			var entry = new Entry
			{
				Placeholder = $"Paste a {platform.DisplayName} link or ID",
				FontSize = 15,
				Keyboard = Keyboard.Url,
				BackgroundColor = Colors.Transparent,
			};
			entry.SetDynamicResource(Entry.TextColorProperty, "ThText");
			entry.SetDynamicResource(Entry.PlaceholderColorProperty, "ThText3");
			var border = new Border
			{
				StrokeThickness = 0,
				Padding = new Thickness(10, 0),
				Margin = new Thickness(0, 6, 0, 0),
				StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
				Content = entry,
			};
			border.SetDynamicResource(Border.BackgroundColorProperty, "ThFieldBg");
			section.Add(border);

			var save = new Button
			{
				Text = "Save link",
				FontSize = 16,
				CornerRadius = 12,
				HeightRequest = 48,
				Margin = new Thickness(0, 6, 0, 0),
			};
			save.SetDynamicResource(Button.BackgroundColorProperty, "ThBtn2Bg");
			save.SetDynamicResource(Button.TextColorProperty, "ThAccent");
			section.Add(save);

			var remove = new Button
			{
				Text = "Remove link",
				FontSize = 16,
				CornerRadius = 12,
				HeightRequest = 48,
				IsVisible = false,
			};
			remove.SetDynamicResource(Button.BackgroundColorProperty, "ThBtn2Bg");
			remove.SetDynamicResource(Button.TextColorProperty, "ThPillText");
			section.Add(remove);

			var hint = new Label
			{
				Text = platform.EditorHint,
				FontSize = 13,
				LineBreakMode = LineBreakMode.WordWrap,
				Margin = new Thickness(0, 4, 0, 0),
			};
			hint.SetDynamicResource(Label.TextColorProperty, "ThText3");
			section.Add(hint);

			var row = new LinkEditorRow { Platform = platform, Status = status, Entry = entry, RemoveButton = remove };
			save.Clicked += (s, e) => OnSaveLink(row);
			remove.Clicked += async (s, e) => await OnRemoveLink(row);
			_linkEditors.Add(row);
			LinkEditorStack.Add(section);
		}
	}

	/// <summary>Reflect each platform's saved-link state for this
	/// episode: the user's own override, a link that came with
	/// the app, or none. The entries themselves are left alone
	/// (the user may be mid-paste when a rebind lands).</summary>
	private void BindLinkEditors()
	{
		var store = AppHost.Store;
		var effective = store.LinksFor(_ep);
		foreach (var row in _linkEditors)
		{
			bool hasOverride = store.GetLinkOverride(_ep.Key, row.Platform.Signifier) != null;
			bool hasLink = effective.Any(l => l.Platform == row.Platform.Signifier);
			row.Status.Text = hasOverride
				? "Your link is saved for this episode — Play uses it."
				: hasLink
					? $"This episode's {row.Platform.DisplayName} link came with the app."
					: $"No {row.Platform.DisplayName} link for this episode yet — it can be tracked, but not started from the app on {row.Platform.DisplayName}.";
			row.RemoveButton.IsVisible = hasOverride;
		}
	}

	private void OnSaveLink(LinkEditorRow row)
	{
		var pasted = (row.Entry.Text ?? "").Trim();
		if (pasted.Length == 0)
		{
			PlayResultLabel.Text = $"Paste the episode's {row.Platform.DisplayName} link in its section above first. {row.Platform.EditorHint}";
			return;
		}
		// The Store enforces the platform's ID form strictly: a
		// real link yields its ID; anything else is rejected and
		// nothing is stored.
		if (AppHost.Store.SetLinkOverride(_ep, row.Platform.Signifier, pasted))
		{
			row.Entry.Text = "";
			PlayResultLabel.Text = "Link saved ✓ — this episode can now be started from the app.";
		}
		else
		{
			PlayResultLabel.Text = $"That doesn't look like a {row.Platform.DisplayName} link or ID — nothing was saved. {row.Platform.EditorHint}";
		}
		Bind();
	}

	private async Task OnRemoveLink(LinkEditorRow row)
	{
		var yes = await DisplayAlert("Remove link?",
			$"Your saved {row.Platform.DisplayName} link for this episode will be removed. The episode stays in the watch order — it just can't be started from the app on {row.Platform.DisplayName} until you add a link again.",
			"Remove", "Cancel");
		if (!yes) return;
		AppHost.Store.SetLinkOverride(_ep, row.Platform.Signifier, "");
		PlayResultLabel.Text = "Link removed.";
		Bind();
	}

	private void OnToggleWatched(object sender, EventArgs e)
	{
		AppHost.Store.SetWatched(_ep, !_ep.Watched);
		UpdateWatchedButton();
		Bind();
	}

	private async void OnStartFromHere(object sender, EventArgs e)
	{
		var yes = await DisplayAlert("Start from here?",
			$"This makes {_ep.Headline} your current episode. It un-marks this episode and everything after it; everything before stays watched.",
			"Start from here", "Cancel");
		if (!yes) return;
		AppHost.Store.StartFromHere(_ep);
		Bind();
		PlayResultLabel.Text = "This is now your current episode — it's Up Next on the main page.";
	}

	private async void OnPlayEpisode(object sender, EventArgs e)
	{
		// Resolve first (Decision 1G): the first link on a service
		// the user has whose channel is on the TV. Every failure
		// is a stated reason, never a silent nothing.
		PlayResolution resolution;
		try
		{
			PlayResultLabel.Text = "Finding this episode on your TV…";
			resolution = await AppHost.Player.ResolveTargetAsync(_ep);
		}
		catch (Exception)
		{
			PlayResultLabel.Text = "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
			return;
		}
		if (resolution.Target is not { } target)
		{
			PlayResultLabel.Text = resolution.Kind switch
			{
				// Defensive: the Play button is hidden in the
				// NoLinks / NoSelectedService cases, so these fire
				// only if state changed under the page.
				PlayResolutionKind.NoLinks =>
					"No streaming link for this episode — add yours under “Your streaming links” below, or open the episode on the TV manually.",
				PlayResolutionKind.NoSelectedService =>
					$"This episode is on {JoinNames(resolution.LinkPlatformNames)}, which isn't in My Services — add it in Settings → My Services, or open the episode on the TV manually.",
				PlayResolutionKind.NotInstalled =>
					$"Your TV doesn't have {JoinNames(resolution.LinkPlatformNames)} installed — install it there, or open the episode on the TV manually.",
				_ => "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.",
			};
			return;
		}

		PlayResultLabel.Text = "Running the play sequence — watch the TV.";
		AppHost.Autoplay.NoteManualPlay(); // a manual Play starts a new autoplay session

		try
		{
			var result = await AppHost.Player.PlayAsync(_ep, target, _ep.Headline);
			PlayResultLabel.Text = result.Busy
				? "Already trying — that tap was ignored; nothing extra was sent to the TV."
				: result.AlreadyPlaying
					? $"Already playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)} on the TV. Nothing was sent."
					: result.Playing
						? $"Playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)} on the TV."
						: "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
		}
		catch (Exception)
		{
			PlayResultLabel.Text = "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
		}
		RefreshPlayButton();
	}

	/// <summary>"Netflix", "Netflix or Disney+", "Prime Video,
	/// Netflix or Disney+" — for the resolution messages.</summary>
	private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
	{
		0 => "a streaming service",
		1 => names[0],
		2 => $"{names[0]} or {names[1]}",
		_ => string.Join(", ", names.Take(names.Count - 1)) + $" or {names[^1]}",
	};
}
