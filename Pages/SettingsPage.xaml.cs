using Microsoft.Maui.ApplicationModel.DataTransfer;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// The settings page (reached from the gear on the main page).
/// Since v2.3.0 the page is five collapsible groups — one tappable
/// header row each, the body shown only while the group is open;
/// the open-set is the user's, persisted in the Store
/// (Store.IsSettingsGroupOpen / SetSettingsGroupOpen) and restored
/// on every visit. Only TV starts open on a fresh install:
///  - TV: address + connection check, Find My TV (SSDP / subnet
///    probe), the "TV asks for a profile" toggle for the Play
///    sequence, and the status-check interval (a free-form seconds
///    field since iteration 9).
///  - Universes &amp; services: the universe include switches
///    (Decision 1C, shared with setup's Universes step —
///    UniverseSection), the downloadable-pack manager
///    (PackSection), the per-universe content-type switches
///    (CategorySection — shown only where a universe mixes
///    categories), and the My Services switches.
///  - Playback: Autoplay (on/off, countdown length — a free-form
///    seconds field since iteration 9 — session limit, and how
///    episode endings (credits) are judged) and Background mode
///    (the foreground-service switch, the notifications-permission
///    state Android requires for it, and the battery guidance —
///    production Decision 5: no exemption request ships, plain
///    instructions plus a route to the app's system settings page).
///  - Appearance: "Match system" plus the five themes, and the
///    spoiler guard.
///  - Setup, backup &amp; about: re-run the first-run setup flow
///    (Decision 7); Backup — export / import / start over, exports
///    stamped with the active universe and applying only to their
///    own universe (Decision 1C); About — version, the TVmaze
///    attribution and non-affiliation notice (Decision 1), and
///    "Share diagnostics" (Decision 6).
/// The grouping is layout only: every control, handler and default
/// is what it was when the sections stood in one long scroll.
/// </summary>
public partial class SettingsPage : ContentPage
{
	private bool _loading;

	public SettingsPage()
	{
		InitializeComponent();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await AppHost.Ready;
		// Keep the theme rows truthful if the phone's light/dark mode
		// flips (and a "Match system" selection re-resolves) while
		// this page is open.
		ThemeService.ThemeChanged -= OnThemeChanged;
		ThemeService.ThemeChanged += OnThemeChanged;
		LoadUi();
	}

	private void OnThemeChanged() =>
		MainThread.BeginInvokeOnMainThread(BuildThemeRows);

	/// <summary>The backup section's data line. It speaks for the
	/// ACTIVE universe (Decision 1C), so it is refreshed whenever a
	/// universe toggle may have moved the active pointer.</summary>
	private void RefreshDataLabel()
	{
		var store = AppHost.Store;
		DataLabel.Text = $"{store.Episodes.Count} episodes in watch order · {store.WatchedCount} watched · check-offs and settings are saved on this phone only.";
	}

	// ---- Groups (v2.3.0) ---------------------------------------------
	// The page's five collapsible groups. The Store owns which are
	// open (persisted); this page only renders that state and flips
	// it on a header tap. All section building in LoadUi runs the
	// same whether its group is open or closed — content builds
	// into hidden containers exactly as it built into scrolled-off
	// ones before the grouping.

	private (string Id, VerticalStackLayout Body, Label Chevron)[] GroupViews() => new[]
	{
		(Store.SettingsGroupTv, TvGroupBody, TvGroupChevron),
		(Store.SettingsGroupUniverses, UniversesGroupBody, UniversesGroupChevron),
		(Store.SettingsGroupPlayback, PlaybackGroupBody, PlaybackGroupChevron),
		(Store.SettingsGroupAppearance, AppearanceGroupBody, AppearanceGroupChevron),
		(Store.SettingsGroupSetup, SetupGroupBody, SetupGroupChevron),
	};

	private void ApplyGroupStates()
	{
		var store = AppHost.Store;
		foreach (var (id, body, chevron) in GroupViews())
		{
			bool open = store.IsSettingsGroupOpen(id);
			body.IsVisible = open;
			chevron.Text = open ? "▾" : "▸";
		}
	}

	private void ToggleGroup(string id)
	{
		var store = AppHost.Store;
		store.SetSettingsGroupOpen(id, !store.IsSettingsGroupOpen(id));
		ApplyGroupStates();
	}

	private void OnTvGroupTapped(object sender, TappedEventArgs e) => ToggleGroup(Store.SettingsGroupTv);

	private void OnUniversesGroupTapped(object sender, TappedEventArgs e) => ToggleGroup(Store.SettingsGroupUniverses);

	private void OnPlaybackGroupTapped(object sender, TappedEventArgs e) => ToggleGroup(Store.SettingsGroupPlayback);

	private void OnAppearanceGroupTapped(object sender, TappedEventArgs e) => ToggleGroup(Store.SettingsGroupAppearance);

	private void OnSetupGroupTapped(object sender, TappedEventArgs e) => ToggleGroup(Store.SettingsGroupSetup);

	private void LoadUi()
	{
		_loading = true;
		ApplyGroupStates();
		var store = AppHost.Store;
		IpEntry.Text = store.TvIp;
		PollEntry.Text = store.PollSeconds.ToString();
		PollHintLabel.IsVisible = false;
		CountdownEntry.Text = store.AutoplayCountdownSeconds.ToString();
		CountdownHintLabel.IsVisible = false;
		AutoplaySwitch.IsToggled = store.AutoplayEnabled;
		ProfileSwitch.IsToggled = store.ProfileSelectEnabled;
		SpoilerSwitch.IsToggled = store.SpoilerGuardEnabled;
		BackgroundSwitch.IsToggled = store.BackgroundModeEnabled;
		_loading = false;
		RefreshBackgroundStatus();
		SessionLimitValueLabel.Text = SessionLimitText(store.AutoplaySessionLimit);
		CreditsValueLabel.Text = CreditsText(store.CreditsThreshold);
		RefreshAutoplayNote();
		BuildThemeRows();
		RefreshDataLabel();
		// The universe rows (shared with setup — UniverseSection).
		// Fire-and-forget: the rows build asynchronously (episode
		// counts load datasets), and a toggle refreshes the
		// data-dependent label through the callback.
		_ = UniverseSection.BuildAsync(UniverseList, UniverseNoteLabel, () =>
		{
			RefreshDataLabel();
			// A universe toggled in or out can gain or lose a
			// Content types block (CategorySection hides itself
			// when nothing qualifies).
			_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
		});
		_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
		// The downloadable-pack manager (PackSection — shared
		// with setup's picker). Installing or removing a pack
		// changes the catalogue itself, so the universe rows and
		// the category blocks rebuild through the callback.
		_ = PackSection.BuildAsync(PackList, PackNoteLabel, PackSectionMode.Manager, () =>
		{
			RefreshDataLabel();
			_ = UniverseSection.BuildAsync(UniverseList, UniverseNoteLabel, () =>
			{
				RefreshDataLabel();
				_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
			});
			_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
		}, async (title, message) => await DisplayAlert(title, message, "Remove", "Cancel"));
		// The My Services rows (shared with setup —
		// MyServicesSection), bound to the same Store state.
		MyServicesSection.Build(ServicesList);
		// Data refresh (iteration 7) is automatic on every app open
		// and silent when it fails — this line is its only surface:
		// the last successful update, or until then the last attempt.
		if (store.DataUpdatedAt is { } updated)
		{
			DataRefreshLabel.IsVisible = true;
			DataRefreshLabel.Text = $"Data updated · {updated.LocalDateTime:MMM d, yyyy} — refreshed automatically when the app opens.";
		}
		else if (store.DataRefreshAttemptedAt is { } attempted)
		{
			DataRefreshLabel.IsVisible = true;
			DataRefreshLabel.Text = $"Data last checked · {attempted.LocalDateTime:MMM d, yyyy}";
		}
		else
		{
			DataRefreshLabel.IsVisible = false;
		}
		AboutLabel.Text = $"Version {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})";
	}

	// ---- Setup + diagnostics ------------------------------------------

	/// <summary>"Run setup again" (Decision 7): the first-run flow,
	/// re-opened manually. Nothing about re-running it resets
	/// anything by itself — its own steps confirm before changing a
	/// starting point, exactly as they do on first run.</summary>
	private async void OnRunSetup(object sender, EventArgs e)
		=> await Navigation.PushAsync(new SetupPage());

	/// <summary>"Share diagnostics" (Decision 6): build the redacted
	/// snapshot now (and only now), then show it on the preview page
	/// — the share sheet opens only from there.</summary>
	private async void OnShareDiagnostics(object sender, EventArgs e)
	{
		DiagnosticsButton.IsEnabled = false;
		try
		{
			var snapshot = await DiagnosticsService.BuildSnapshotAsync(AppHost.Store, AppHost.Roku);
			await Navigation.PushAsync(new DiagnosticsPage(snapshot));
		}
		catch (Exception ex)
		{
			await DisplayAlert("Diagnostics failed", $"The snapshot couldn't be built ({ex.Message}).", "OK");
		}
		finally
		{
			DiagnosticsButton.IsEnabled = true;
		}
	}

	// ---- TV ----------------------------------------------------------

	/// <summary>Commit the TV address field. Only a valid local
	/// address (Store.TryValidateTvAddress — the production rule,
	/// audit 1.3) is ever saved; an empty field clears the address.
	/// Invalid text is NOT saved, the previous address stays, and
	/// the user is told why. Returns false only for invalid text.</summary>
	private bool SaveIp()
	{
		var ip = (IpEntry.Text ?? "").Trim();
		if (ip.Length == 0)
		{
			if (AppHost.Store.TvIp.Length > 0)
			{
				AppHost.Store.TvIp = "";
				AppHost.Store.SaveSettings();
			}
			return true;
		}
		if (!Store.TryValidateTvAddress(ip, out var validIp))
		{
			CheckResultLabel.Text = "That doesn't look like a TV address — it should be the TV's local IP address (like 192.168.1.50). The previous address was kept.";
			return false;
		}
		if (validIp != AppHost.Store.TvIp)
		{
			AppHost.Store.TvIp = validIp;
			AppHost.Store.SaveSettings();
		}
		return true;
	}

	private async void OnCheckConnection(object sender, EventArgs e)
	{
		if (!SaveIp()) return;
		if (AppHost.Store.TvIp.Length == 0)
		{
			CheckResultLabel.Text = "No TV address is set yet — tap Find my TV, or type the TV's address above, then tap Check connection.";
			return;
		}
		CheckResultLabel.Text = "Asking the TV…";
		var info = await AppHost.Roku.GetDeviceInfoSummaryAsync();
		CheckResultLabel.Text = info != null
			? $"✓ TV answered: {info}"
			: "✗ No answer. Check: phone on the home Wi-Fi, VPN off, address correct, and on the TV: Settings → System → Advanced system settings → Control by mobile apps → Network access = Permissive.";
	}

	private async void OnFindTv(object sender, EventArgs e)
	{
		FindTvButton.IsEnabled = false;
		FoundList.Clear();
		try
		{
			var results = await RokuFinder.FindAsync(
				s => MainThread.BeginInvokeOnMainThread(() => FindStatusLabel.Text = s));
			if (results.Count == 0)
			{
				FindStatusLabel.Text = "No Roku found. Make sure the TV is on and the phone is on the same Wi-Fi, or type the address in by hand.";
				return;
			}
			FindStatusLabel.Text = results.Count == 1
				? "Found one — tap it to use its address:"
				: $"Found {results.Count} — tap yours to use its address:";
			foreach (var tv in results)
			{
				var row = new VerticalStackLayout { Padding = new Thickness(0, 8) };
				var ipLabel = new Label { Text = tv.Ip, FontSize = 16, FontAttributes = FontAttributes.Bold };
				ipLabel.SetDynamicResource(Label.TextColorProperty, "ThText");
				var sumLabel = new Label { Text = tv.Summary, FontSize = 13 };
				sumLabel.SetDynamicResource(Label.TextColorProperty, "ThText2");
				row.Add(ipLabel);
				row.Add(sumLabel);
				row.GestureRecognizers.Add(new TapGestureRecognizer
				{
					Command = new Command(() =>
					{
						IpEntry.Text = tv.Ip;
						SaveIp();
						FindStatusLabel.Text = $"Address set to {tv.Ip} — tap Check connection to check it.";
					}),
				});
				FoundList.Add(row);
			}
		}
		catch (Exception ex)
		{
			FindStatusLabel.Text = $"The search hit a problem ({ex.Message}). Typing the address by hand still works.";
		}
		finally
		{
			FindTvButton.IsEnabled = true;
		}
	}

	private void OnProfileToggled(object sender, ToggledEventArgs e)
	{
		if (_loading) return;
		AppHost.Store.SetProfileSelectEnabled(e.Value);
	}

	// TV check interval (iteration 9): a free-form seconds field.
	// Saves when the entry is completed (keyboard Done) and when it
	// loses focus; invalid text restores the last valid value and
	// shows the inline hint — no dialogs.

	private void OnPollEntryCompleted(object sender, EventArgs e) => CommitPollSeconds();

	private void OnPollEntryUnfocused(object sender, FocusEventArgs e) => CommitPollSeconds();

	private void CommitPollSeconds()
	{
		if (_loading) return;
		var store = AppHost.Store;
		if (Store.TryParsePollSeconds(PollEntry.Text, out var seconds))
		{
			PollHintLabel.IsVisible = false;
			if (seconds != store.PollSeconds)
				store.SetPollSeconds(seconds);
			PollEntry.Text = seconds.ToString();
		}
		else
		{
			PollEntry.Text = store.PollSeconds.ToString();
			PollHintLabel.IsVisible = true;
		}
	}

	// ---- Autoplay ------------------------------------------------------

	private void OnAutoplayToggled(object sender, ToggledEventArgs e)
	{
		if (_loading) return;
		AppHost.Store.SetAutoplayEnabled(e.Value);
	}

	private void RefreshAutoplayNote()
	{
		var store = AppHost.Store;
		AutoplayNoteLabel.Text = store.AutoplayCountdownSeconds == 0
			? "When an episode finishes on the TV, the app plays the next one in the watch order straight away — no countdown. Autoplay runs while the app is open — and in the background too, when Background mode (below) is on. Turn OFF each streaming app's own Auto Play (in that app's settings on the TV) so this app is the one driving episode changes."
			: $"When an episode finishes on the TV, the app counts down {store.AutoplayCountdownSeconds} seconds and plays the next one in the watch order. Autoplay runs while the app is open — and in the background too, when Background mode (below) is on. Turn OFF each streaming app's own Auto Play (in that app's settings on the TV) so this app is the one driving episode changes.";
	}

	// Autoplay countdown (iteration 9): a free-form seconds field —
	// 0 means the next episode starts immediately. Same commit rules
	// as the TV check interval: save on completion and on losing
	// focus; invalid text restores the last valid value + hint.

	private void OnCountdownEntryCompleted(object sender, EventArgs e) => CommitCountdownSeconds();

	private void OnCountdownEntryUnfocused(object sender, FocusEventArgs e) => CommitCountdownSeconds();

	private void CommitCountdownSeconds()
	{
		if (_loading) return;
		var store = AppHost.Store;
		if (Store.TryParseCountdownSeconds(CountdownEntry.Text, out var seconds))
		{
			CountdownHintLabel.IsVisible = false;
			if (seconds != store.AutoplayCountdownSeconds)
			{
				store.SetAutoplayCountdownSeconds(seconds);
				RefreshAutoplayNote();
			}
			CountdownEntry.Text = seconds.ToString();
		}
		else
		{
			CountdownEntry.Text = store.AutoplayCountdownSeconds.ToString();
			CountdownHintLabel.IsVisible = true;
		}
	}

	private static string SessionLimitText(int limit) => limit switch
	{
		0 => "No limit ›",
		1 => "1 episode ›",
		_ => $"{limit} episodes ›",
	};

	private async void OnSessionLimitRowTapped(object sender, TappedEventArgs e)
	{
		var labels = new Dictionary<string, int>
		{
			["No limit"] = 0, ["1 episode"] = 1, ["2 episodes"] = 2, ["3 episodes"] = 3, ["5 episodes"] = 5,
		};
		var pick = await DisplayActionSheet("Stop autoplay after", "Cancel", null, labels.Keys.ToArray());
		if (pick == null || !labels.TryGetValue(pick, out var limit)) return;
		AppHost.Store.SetAutoplaySessionLimit(limit);
		LoadUi();
	}

	private static string CreditsText(string mode) => mode switch
	{
		Store.CreditsLetPlay => "Let credits play ›",
		_ => "Balanced ›",
	};

	private async void OnCreditsRowTapped(object sender, TappedEventArgs e)
	{
		var labels = new Dictionary<string, string>
		{
			["Let credits play"] = Store.CreditsLetPlay,
			["Balanced"] = Store.CreditsBalanced,
		};
		var pick = await DisplayActionSheet("Episode endings", "Cancel", null, labels.Keys.ToArray());
		if (pick == null || !labels.TryGetValue(pick, out var mode)) return;
		AppHost.Store.SetCreditsThreshold(mode);
		LoadUi();
	}

	// ---- Background mode -------------------------------------------------

	private void OnBackgroundToggled(object sender, ToggledEventArgs e)
	{
		if (_loading) return;
		AppHost.Store.SetBackgroundModeEnabled(e.Value);
#if ANDROID
		// The settings page is a foreground context, so starting the
		// service here respects Android's background-start rules.
		if (e.Value) OneChicago.Platforms.MonitorServiceController.EnsureStarted();
		else OneChicago.Platforms.MonitorServiceController.StopService();
#endif
		RefreshBackgroundStatus();
	}

	/// <summary>The honest platform state for background mode: the
	/// notifications permission Android makes mandatory for a
	/// foreground service, and the battery guidance (Decision 5 —
	/// the instructions are static text in the XAML; this only
	/// shows them, and the app-settings button, while background
	/// mode is on, which is the only time they matter). Refreshed
	/// every time the page appears.</summary>
	private void RefreshBackgroundStatus()
	{
#if ANDROID
		bool backgroundOn = AppHost.Store.BackgroundModeEnabled;
		bool notifOk = OneChicago.Platforms.MonitorServiceController.NotificationsAllowed();
		NotificationStatusLabel.IsVisible = backgroundOn && !notifOk;
		if (backgroundOn && !notifOk)
		{
			NotificationStatusLabel.Text = "⚠ Notifications are off for this app. Android requires the visible notification for a foreground service, so background mode can't be protected until notifications are allowed — in your phone's Settings → Apps → One Timeline → Notifications.";
		}
		BatteryStatusLabel.IsVisible = backgroundOn;
		AppSettingsButton.IsVisible = backgroundOn;
#endif
	}

	private void OnOpenAppSettings(object sender, EventArgs e)
	{
#if ANDROID
		OneChicago.Platforms.MonitorServiceController.OpenAppSettings();
#endif
	}

	// ---- Appearance ----------------------------------------------------

	private static Border SwatchDot(Color color) => new()
	{
		BackgroundColor = color,
		Stroke = Color.FromArgb("#8E8E93"),
		StrokeThickness = 0.5,
		WidthRequest = 18,
		HeightRequest = 18,
		StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(9) },
	};

	private static Label ThemeRowName(string text)
	{
		var label = new Label { Text = text, FontSize = 16, VerticalOptions = LayoutOptions.Center };
		label.SetDynamicResource(Label.TextColorProperty, "ThText");
		return label;
	}

	private static Label ThemeRowCheck(bool active)
	{
		var check = new Label
		{
			Text = active ? "✓" : "",
			FontSize = 17,
			FontAttributes = FontAttributes.Bold,
			VerticalOptions = LayoutOptions.Center,
		};
		check.SetDynamicResource(Label.TextColorProperty, "ThAccent");
		return check;
	}

	private static Grid ThemeRowGrid()
	{
		var row = new Grid
		{
			ColumnDefinitions = new ColumnDefinitionCollection
			{
				new ColumnDefinition(GridLength.Auto),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto),
			},
			Padding = new Thickness(0, 10),
			ColumnSpacing = 12,
		};
		return row;
	}

	private void AddThemeHairline()
	{
		var hair = new BoxView { HeightRequest = 0.5 };
		hair.SetDynamicResource(VisualElement.BackgroundColorProperty, "ThHairline");
		ThemeList.Add(hair);
	}

	private void BuildThemeRows()
	{
		ThemeList.Clear();

		// "Match system" (iteration 9) — always the first row. The
		// check lives on the SELECTION: in system mode this row is
		// checked and no named theme is, even though the resolved
		// palette is one of the Basics below.
		{
			bool systemActive = ThemeService.IsSystemSelected;

			var swatches = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
			swatches.Add(SwatchDot(ThemeService.Light.Swatches[0]));
			swatches.Add(SwatchDot(ThemeService.Dark.Swatches[0]));

			var texts = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 1 };
			texts.Add(ThemeRowName("Match system"));
			var sub = new Label
			{
				Text = systemActive
					? $"Using {ThemeService.Current.Name}"
					: "Follow the phone's light/dark setting",
				FontSize = 13,
			};
			sub.SetDynamicResource(Label.TextColorProperty, "ThText3");
			texts.Add(sub);

			var row = ThemeRowGrid();
			row.Add(swatches, 0, 0);
			row.Add(texts, 1, 0);
			row.Add(ThemeRowCheck(systemActive), 2, 0);
			row.GestureRecognizers.Add(new TapGestureRecognizer
			{
				Command = new Command(() =>
				{
					ThemeService.ApplySelection(ThemeService.SystemId);
					AppHost.Store.SetThemeId(ThemeService.SystemId);
					BuildThemeRows();
				}),
			});
			ThemeList.Add(row);
			AddThemeHairline();
		}

		foreach (var theme in ThemeService.All)
		{
			bool active = !ThemeService.IsSystemSelected && theme.Id == ThemeService.Current.Id;

			var swatches = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
			foreach (var c in theme.Swatches)
				swatches.Add(SwatchDot(c));

			var row = ThemeRowGrid();
			row.Add(swatches, 0, 0);
			row.Add(ThemeRowName(theme.Name), 1, 0);
			row.Add(ThemeRowCheck(active), 2, 0);
			var captured = theme;
			row.GestureRecognizers.Add(new TapGestureRecognizer
			{
				Command = new Command(() =>
				{
					ThemeService.ApplySelection(captured.Id);
					AppHost.Store.SetThemeId(captured.Id);
					BuildThemeRows();
				}),
			});
			ThemeList.Add(row);
			AddThemeHairline();
		}
	}

	private void OnSpoilerToggled(object sender, ToggledEventArgs e)
	{
		if (_loading) return;
		AppHost.Store.SetSpoilerGuardEnabled(e.Value);
	}

	// ---- Backup ----------------------------------------------------------

	private async void OnExport(object sender, EventArgs e)
	{
		try
		{
			var path = Path.Combine(FileSystem.CacheDirectory, "onechicago-progress.json");
			File.WriteAllText(path, AppHost.Store.ExportStateJson());
			await Share.Default.RequestAsync(new ShareFileRequest
			{
				Title = "One Timeline progress backup",
				File = new ShareFile(path),
			});
		}
		catch (Exception ex)
		{
			await DisplayAlert("Export failed", $"The backup couldn't be created ({ex.Message}).", "OK");
		}
	}

	private async void OnImport(object sender, EventArgs e)
	{
		try
		{
			var result = await FilePicker.Default.PickAsync(new PickOptions
			{
				PickerTitle = "Choose a One Timeline progress backup",
				FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
				{
					{ DevicePlatform.Android, new[] { "application/json" } },
				}),
			});
			if (result == null) return;

			string json;
			try
			{
				// Bounded read (audit 1.5): a huge or hostile file is
				// refused at 2 MB instead of being read whole into
				// memory before any validation runs.
				using var stream = await result.OpenReadAsync();
				json = await BoundedRead.ReadStreamStringAsync(stream, BoundedRead.ImportCapBytes);
			}
			catch (InvalidDataException)
			{
				await DisplayAlert("Couldn't import", "That file is too large to be a One Timeline progress backup (2 MB limit). Nothing was changed.", "OK");
				return;
			}

			var yes = await DisplayAlert("Import progress?",
				"This replaces your watched episodes and settings with the backup's. It can't be undone — export your current progress first if you might want it back.",
				"Import", "Cancel");
			if (!yes) return;

			var importResult = AppHost.Store.ImportStateJson(json);
			if (importResult == Store.ImportResult.Ok)
			{
				// ApplySelection (not a bare palette apply): a backup
				// carrying the "system" selection restores Match
				// system, resolved from the phone's current mode.
				ThemeService.ApplySelection(AppHost.Store.ThemeId);
				LoadUi();
				await DisplayAlert("Imported", "Your progress now matches the backup.", "OK");
			}
			else if (importResult == Store.ImportResult.WrongUniverse)
			{
				// A good backup, but for another universe (Decision
				// 1C): a backup only ever applies to its own
				// universe's watch order. (DisplayAlertAsync: the
				// non-obsolete form, so this new call adds no new
				// warning to the file's existing set.)
				await DisplayAlertAsync("Different universe",
					"That backup was exported from a different universe than the one you're viewing, and a backup only ever applies to its own universe's watch order. Switch to that universe and import it there. Nothing was changed.", "OK");
			}
			else
			{
				await DisplayAlert("Couldn't import", "That file doesn't look like a One Timeline progress backup. Nothing was changed.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Import failed", $"The backup couldn't be read ({ex.Message}). Nothing was changed.", "OK");
		}
	}

	private async void OnStartOver(object sender, EventArgs e)
	{
		var first = await DisplayAlert("Start over?",
			"This wipes your watched episodes and every setting, and returns the app to a first install — nothing watched, and setup will be offered again.",
			"Continue", "Cancel");
		if (!first) return;
		var second = await DisplayAlert("Are you sure?",
			"There is no undo. If you might want this progress back, cancel and use Export progress first.",
			"Erase everything", "Keep my progress");
		if (!second) return;

		await AppHost.Store.ClearAllAsync();
		// The reset put ThemeId back to "light" — apply it as a
		// SELECTION so a previous "system" selection doesn't linger
		// in ThemeService and re-resolve later.
		ThemeService.ApplySelection(AppHost.Store.ThemeId);
		LoadUi();
		await DisplayAlert("Done", "The app is back to a fresh start.", "OK");
	}

	protected override void OnDisappearing()
	{
		// Last-chance commits for the free-form seconds fields — the
		// entries' own Unfocused handlers normally beat us here, and
		// these are idempotent when the text already matches.
		CommitPollSeconds();
		CommitCountdownSeconds();
		ThemeService.ThemeChanged -= OnThemeChanged;
		SaveIp();
		base.OnDisappearing();
	}
}
