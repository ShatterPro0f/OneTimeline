using OneChicago.Models;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// Clean episode detail (pushed from the main page): title, meta,
/// description (TVmaze, credited on the page), cameos, crossover
/// context — and the actions: Play, Start from here, Mark watched.
///
/// Prime Video links: the launch universe ships WITH its episode
/// links (Decision 1 as amended 2026-11-03). This page carries the
/// "Your Prime Video link" editor on top of them: the user pastes
/// a link they copied from Prime Video themselves, the Store
/// validates it strictly and keeps it as their override (the top
/// precedence layer), and it can be removed again here. An episode
/// with no link from any layer shows no Play button — it is
/// tracker-only, exactly like an episode Prime doesn't carry.
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

		BindPrimeLink();
		PlayEpisodeButton.IsVisible = !string.IsNullOrWhiteSpace(AppHost.Store.PrimeIdFor(_ep));

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

	// ---- Your Prime Video link --------------------------------------

	/// <summary>Reflect the saved-link state: whether this episode
	/// has one of the user's own links, and whether it can be
	/// removed. The entry itself is left alone (the user may be
	/// mid-paste when a rebind lands).</summary>
	private void BindPrimeLink()
	{
		var store = AppHost.Store;
		bool hasOverride = store.GetPrimeOverride(_ep.Key) != null;
		PrimeLinkStatusLabel.Text = hasOverride
			? "Your link is saved for this episode — Play uses it."
			: string.IsNullOrWhiteSpace(store.PrimeIdFor(_ep))
				? "No link saved for this episode yet — it can be tracked, but not started from the app."
				: "This episode's Prime Video link came with the app.";
		RemovePrimeLinkButton.IsVisible = hasOverride;
	}

	private void OnSavePrimeLink(object sender, EventArgs e)
	{
		var pasted = (PrimeLinkEntry.Text ?? "").Trim();
		if (pasted.Length == 0)
		{
			PlayResultLabel.Text = "Paste the episode's Prime Video link above first — in Prime Video, open the episode, tap Share, then Copy link.";
			return;
		}
		// The Store enforces the strict ID format (audit 1.4): a
		// real Prime link yields its episode ID; anything else is
		// rejected and nothing is stored.
		if (AppHost.Store.SetPrimeContentId(_ep, pasted))
		{
			PrimeLinkEntry.Text = "";
			PlayResultLabel.Text = "Link saved ✓ — this episode can now be started from the app.";
		}
		else
		{
			PlayResultLabel.Text = "That doesn't look like a Prime Video episode link — nothing was saved. Copy the link from the episode's own page in Prime Video and try again.";
		}
		Bind();
	}

	private async void OnRemovePrimeLink(object sender, EventArgs e)
	{
		var yes = await DisplayAlert("Remove link?",
			"Your saved Prime Video link for this episode will be removed. The episode stays in the watch order — it just can't be started from the app until you add a link again.",
			"Remove", "Cancel");
		if (!yes) return;
		AppHost.Store.SetPrimeContentId(_ep, "");
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
		if (string.IsNullOrWhiteSpace(AppHost.Store.PrimeIdFor(_ep)))
		{
			// Defensive: the Play button is hidden without a link,
			// so this only fires if state changed under the page.
			PlayResultLabel.Text = "No Prime Video link saved for this episode — add yours under “Your Prime Video link” below, or open the episode on the TV manually.";
			return;
		}

		PlayResultLabel.Text = "Running the play sequence — watch the TV.";
		AppHost.Autoplay.NoteManualPlay(); // a manual Play starts a new autoplay session

		try
		{
			var result = await AppHost.Player.PlayAsync(_ep, AppHost.Store.PrimeIdFor(_ep), _ep.Headline);
			PlayResultLabel.Text = result.Busy
				? "Already trying — that tap was ignored; nothing extra was sent to the TV."
				: result.AlreadyPlaying
					? $"Already playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)} on the TV. Nothing was sent."
					: result.Playing
						? $"Playing ✅ — at {GuidedPlayer.FormatPosition(result.PositionMs ?? 0)} on the TV."
						: "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
		}
		catch (Exception ex)
		{
			PlayResultLabel.Text = "Didn't start. Check the TV is on and the phone is on the same Wi-Fi, then try again.";
		}
		RefreshPlayButton();
	}
}
