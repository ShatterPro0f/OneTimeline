using OneChicago.Models;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// First-run setup (production Decision 7) — also re-runnable from
/// Settings via "Run setup again". Optional steps on one clean
/// page:
///
///  0. Choose your universes (v2.1.0, Decision 1J). The app ships
///     with NO universes; this is the PackSection picker — pick
///     packs from the downloadable index and download them, or
///     skip entirely and add them later in Settings. Everything
///     below is scoped to whatever actually got installed.
///  1. Universes (Decision 1C). Which of the installed packs to
///     include — the rows are UniverseSection's, shared with
///     Settings. This comes before the starting point because
///     that step is per-universe: it speaks for whichever
///     universe is active.
///  2. Starting point. Nobody is silently seeded: a fresh install
///     starts unwatched, and this step asks whether the user has
///     already watched some of the shows and lets them pick the
///     first unwatched episode (everything before it is marked
///     watched — the same StartFromHere the episode pages use).
///  3. TV connection. Find My TV / manual entry + Check connection,
///     the same pieces Settings uses (RokuFinder, the Store's
///     address validation, RokuClient's device summary).
///  4. My Services (2026-11-04). Which of Prime Video, Netflix
///     and Disney+ the user actually has — the rows are
///     MyServicesSection's, shared with Settings. Play only ever
///     uses a picked service.
///  5. Streaming links. The packs ship with their links
///     (Decision 1 as amended); this step explains how a user
///     can replace a single episode's link with their own on
///     that episode's page.
///
/// Done and Skip both mark setup completed (Store.SetupCompleted),
/// so it is never auto-offered again; backing out without either
/// leaves it offered on the next launch, which is the honest state
/// for an unfinished setup.
/// </summary>
public partial class SetupPage : ContentPage
{
	public SetupPage()
	{
		InitializeComponent();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await AppHost.Ready;
		IpEntry.Text = AppHost.Store.TvIp;
		RefreshStartingStatus();
		BuildStartingResults();
		// Whenever the universe selection moves — a toggle below,
		// or a pack downloaded/removed above — the starting-point
		// step speaks for the active universe: refresh its status,
		// clear any search results (they were the old universe's
		// episodes), and rebuild the Content types blocks.
		void OnUniverseSelectionChanged()
		{
			StartingSearchEntry.Text = "";
			BuildStartingResults();
			RefreshStartingStatus();
			_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
		}
		// Step one (v2.1.0): the pack picker (PackSection —
		// shared with Settings' pack manager). Setup starts
		// EMPTY; downloading here is what puts universes into
		// the rows below.
		_ = PackSection.BuildAsync(PackList, PackNoteLabel, PackSectionMode.Picker, () =>
		{
			_ = UniverseSection.BuildAsync(UniverseList, UniverseNoteLabel, OnUniverseSelectionChanged);
			OnUniverseSelectionChanged();
		});
		// The universe rows (shared with Settings — UniverseSection).
		await UniverseSection.BuildAsync(UniverseList, UniverseNoteLabel, OnUniverseSelectionChanged);
		_ = CategorySection.BuildAsync(CategoryList, CategoryHeaderLabel, CategoryExplainerLabel);
		// The My Services rows (shared with Settings —
		// MyServicesSection), bound to the same Store state.
		MyServicesSection.Build(ServicesList);
	}

	// ---- Starting point ----------------------------------------------

	private void RefreshStartingStatus()
	{
		var store = AppHost.Store;
		if (!store.Loaded) return;
		var next = store.UpNext;
		StartingStatusLabel.Text = store.ActiveUniverseId.Length == 0
			? "No universes yet — download some in the step above, or add them later in Settings, under “Get more universes”."
			: next == null
				? "Everything is currently marked watched."
				: store.WatchedCount == 0
					? "Right now you're at the very beginning — nothing is marked watched."
					: $"Right now, {next.Headline} is up next ({store.WatchedCount} episodes marked watched).";
	}

	private void BuildStartingResults()
	{
		StartingResults.Clear();
		var store = AppHost.Store;
		if (!store.Loaded) return;
		var q = (StartingSearchEntry.Text ?? "").Trim().ToLowerInvariant();
		if (q.Length == 0) return; // no query, no list — the page stays clean

		var matches = store.Episodes
			.Where(e => $"{e.Title} {e.Code} {e.Show} {e.DisplayShow} {e.Order}".ToLowerInvariant().Contains(q))
			.Take(30)
			.ToList();
		if (matches.Count == 0)
		{
			var none = new Label
			{
				Text = "No episodes match that search.",
				FontSize = 14,
				Padding = new Thickness(0, 8),
			};
			none.SetDynamicResource(Label.TextColorProperty, "ThText2");
			StartingResults.Add(none);
			return;
		}
		foreach (var ep in matches)
		{
			var row = new VerticalStackLayout { Padding = new Thickness(0, 8), Spacing = 1 };
			var title = new Label { Text = ep.Title, FontSize = 16 };
			title.SetDynamicResource(Label.TextColorProperty, "ThText");
			var sub = new Label
			{
				Text = $"{ep.DisplayShow} · {ep.SeasonEpisode}" + (ep.AirDateDisplay.Length > 0 ? $" · {ep.AirDateDisplay}" : ""),
				FontSize = 13,
			};
			sub.SetDynamicResource(Label.TextColorProperty, "ThText3");
			row.Add(title);
			row.Add(sub);
			var captured = ep;
			row.GestureRecognizers.Add(new TapGestureRecognizer
			{
				Command = new Command(async () => await PickStartingPoint(captured)),
			});
			StartingResults.Add(row);
		}
		if (matches.Count == 30)
		{
			var more = new Label { Text = "Keep typing to narrow it down…", FontSize = 13, Padding = new Thickness(0, 6) };
			more.SetDynamicResource(Label.TextColorProperty, "ThText3");
			StartingResults.Add(more);
		}
	}

	private void OnStartingSearchChanged(object sender, TextChangedEventArgs e) => BuildStartingResults();

	private async Task PickStartingPoint(Episode ep)
	{
		var yes = await DisplayAlert("Set your starting point?",
			$"“{ep.Title}” ({ep.DisplayShow} {ep.Code}) becomes your current episode: it and everything after it are unwatched, and everything before it is marked watched.",
			"Set starting point", "Cancel");
		if (!yes) return;
		AppHost.Store.StartFromHere(ep);
		StartingSearchEntry.Text = "";
		BuildStartingResults();
		RefreshStartingStatus();
	}

	private async void OnStartFromBeginning(object sender, EventArgs e)
	{
		var store = AppHost.Store;
		if (!store.Loaded || store.Episodes.Count == 0) return;
		if (store.WatchedCount > 0)
		{
			var yes = await DisplayAlert("Start from the beginning?",
				$"This un-marks all {store.WatchedCount} watched episodes. Your settings and any saved links are kept.",
				"Start from the beginning", "Cancel");
			if (!yes) return;
		}
		store.StartFromHere(store.Episodes[0]);
		RefreshStartingStatus();
	}

	// ---- TV connection (the Settings pieces, in setup order) ---------

	/// <summary>Commit the TV address field under the one production
	/// rule (Store.TryValidateTvAddress): only a valid local address
	/// is ever saved; an empty field clears it; invalid text is not
	/// saved and the user is told why.</summary>
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

	// ---- Finish ---------------------------------------------------------

	/// <summary>"Skip for now" on the pack picker: downloading
	/// is step one, never a gate — the rest of setup works with
	/// no universes at all, and packs can be added any time in
	/// Settings. The button just records that the user saw the
	/// choice and points at where it lives later.</summary>
	private void OnSkipPacks(object sender, EventArgs e)
	{
		PackNoteLabel.Text = "No universes downloaded — that's fine. You can add them any time in Settings, under “Get more universes”.";
		PackNoteLabel.IsVisible = true;
		SkipPacksButton.IsVisible = false;
	}

	private async void OnDone(object sender, EventArgs e)
	{
		SaveIp();
		AppHost.Store.SetSetupCompleted();
		await Navigation.PopAsync();
	}

	private async void OnSkip(object sender, EventArgs e)
	{
		AppHost.Store.SetSetupCompleted();
		await Navigation.PopAsync();
	}
}
