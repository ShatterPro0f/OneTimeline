using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>Which face of the pack component a host is showing:
/// the setup picker's multi-select download list, or Settings'
/// full manager.</summary>
public enum PackSectionMode
{
	Picker,
	Manager,
}

/// <summary>
/// The downloadable-pack component (Decision 1J; Sam's
/// rulings, 2026-11-04) — ONE component, two hosts, following
/// the UniverseSection sharing pattern:
///
///  - SETUP (Picker mode): the first step of first-run setup.
///    The app ships with no universes; this lists the pack
///    index with checkboxes — name, shows line, episode count,
///    download size — and a "Download selected" button that
///    works through the picks one by one, each row reporting
///    its own progress and, on failure, its own plain reason
///    and Retry button. Skipping is the host page's own button:
///    packs can always be added later, never a trap.
///  - SETTINGS (Manager mode): the same list as direct
///    Download buttons, plus the installed downloads (data
///    version, "Update available" when the index is newer, and
///    Remove — which keeps the universe's progress, and says
///    so), plus any PENDING universes (wanted by an upgraded
///    install, pack not on the device) as "needs download"
///    rows with a download button, or a Forget button when the
///    index no longer offers them.
///
/// The index comes from PackService (network first, last-good
/// cache offline); a total fetch failure shows one plain line
/// and the local groups still render. Nothing here can crash
/// its host: a bad index renders as "couldn't get the list".
/// </summary>
public static class PackSection
{
	// Reentrancy guards, same as the sibling sections: a build
	// replaces rows wholesale, and a download finishing triggers
	// a rebuild through the host callback.
	private static bool _building;
	private static bool _downloading;

	private sealed class RowHandle
	{
		public required PackIndexEntry Entry;
		public required Label Status;
		public Button? Action;   // manager: Download/Retry; picker: failure Retry (hidden until needed)
		public CheckBox? Check;
	}

	public static async Task BuildAsync(
		VerticalStackLayout host,
		Label? noteLabel,
		PackSectionMode mode,
		Action? onPacksChanged = null,
		Func<string, string, Task<bool>>? confirmAsync = null)
	{
		if (_building) return;
		_building = true;
		try
		{
			host.Clear();
			if (noteLabel != null) noteLabel.IsVisible = false;
			var store = AppHost.Store;

			// ---- The index (network or last-good cache) ----
			var checking = new Label { Text = "Checking for more universes…", FontSize = 14 };
			checking.SetDynamicResource(Label.TextColorProperty, "ThText2");
			host.Add(checking);
			var (entries, fromCache) = await PackService.FetchIndexAsync();
			host.Remove(checking);
			var byId = entries?.ToDictionary(e => e.Id) ?? new Dictionary<string, PackIndexEntry>();

			// ---- Pending: wanted, but the pack isn't here ----
			var pending = store.PendingRestoreIds;
			if (pending.Count > 0)
			{
				AddHeader(host, "NEEDS DOWNLOAD");
				AddNote(host, "This install had these universes before, but their episode lists aren't on this phone yet. Download them and your progress is right where you left it — it was never touched.");
				foreach (var id in pending)
				{
					byId.TryGetValue(id, out var entry);
					AddPendingRow(host, store, id, entry, onPacksChanged);
				}
			}

			// ---- Installed downloads (manager face) ----
			var installed = store.InstalledPacks;
			if (mode == PackSectionMode.Manager && installed.Count > 0)
			{
				AddHeader(host, "DOWNLOADED UNIVERSES");
				foreach (var record in installed)
				{
					byId.TryGetValue(record.Id, out var entry);
					AddInstalledRow(host, store, record, entry, onPacksChanged, confirmAsync);
				}
			}
			if (mode == PackSectionMode.Picker && installed.Count > 0)
			{
				AddHeader(host, "ALREADY INSTALLED");
				foreach (var record in installed)
					AddStaticRow(host, record.DisplayName, record.ShowsLine, "Installed — it will appear in the universes list below.");
			}

			// ---- Available: in the index, not installed ----
			var available = entries?
				.Where(e => store.FindInstalledPack(e.Id) == null && !pending.Contains(e.Id))
				.ToList();
			if (entries == null)
			{
				AddNote(host, "Couldn't get the universe list — check your connection, then reopen this page to try again.");
			}
			else if (available is { Count: > 0 })
			{
				AddHeader(host, mode == PackSectionMode.Picker ? "CHOOSE YOUR UNIVERSES" : "AVAILABLE TO DOWNLOAD");
				if (fromCache)
					AddNote(host, "Showing the list from your last check — couldn't fetch a newer one just now.");
				// Download sizes are a courtesy (HEAD requests);
				// whatever doesn't answer simply shows no size.
				var sizes = new Dictionary<string, long?>();
				try
				{
					var probes = available.Select(async e => (e.Id, Size: await PackService.ProbeSizeAsync(e))).ToList();
					foreach (var probe in await Task.WhenAll(probes)) sizes[probe.Id] = probe.Size;
				}
				catch { /* sizes are optional */ }

				var rows = new List<RowHandle>();
				var selected = new HashSet<string>();
				Button? downloadSelected = null;
				foreach (var entry in available)
				{
					sizes.TryGetValue(entry.Id, out var size);
					rows.Add(AddAvailableRow(host, store, entry, size, mode, selected,
						() => RefreshDownloadSelected(downloadSelected, selected),
						onPacksChanged));
				}
				if (mode == PackSectionMode.Picker)
				{
					downloadSelected = new Button
					{
						Text = "Download selected",
						FontSize = 16,
						CornerRadius = 12,
						HeightRequest = 48,
						Margin = new Thickness(0, 8, 0, 0),
						IsEnabled = false,
					};
					downloadSelected.SetDynamicResource(Button.BackgroundColorProperty, "ThAccent");
					downloadSelected.SetDynamicResource(Button.TextColorProperty, "ThAccentText");
					var capturedRows = rows;
					downloadSelected.Clicked += async (s, e) =>
					{
						if (_downloading) return;
						var picks = capturedRows.Where(r => selected.Contains(r.Entry.Id)).ToList();
						if (picks.Count == 0) return;
						_downloading = true;
						downloadSelected.IsEnabled = false;
						foreach (var row in picks)
						{
							if (row.Check != null) row.Check.IsEnabled = false;
							await DownloadOneAsync(store, row, onPacksChanged);
						}
						_downloading = false;
						onPacksChanged?.Invoke();
						_ = BuildAsync(host, noteLabel, mode, onPacksChanged, confirmAsync);
					};
					host.Add(downloadSelected);
					AddNote(host, "Nothing is final: universes can be added or removed any time in Settings, and removing one never deletes your progress in it.");
				}
			}
			else if (installed.Count == 0 && pending.Count == 0)
			{
				AddNote(host, "No universes are available to download right now.");
			}
		}
		finally
		{
			_building = false;
		}
	}

	// ---- Row builders ------------------------------------------

	private static void AddHeader(VerticalStackLayout host, string text)
	{
		var header = new Label
		{
			Text = text,
			FontSize = 12,
			FontAttributes = FontAttributes.Bold,
			CharacterSpacing = 1.2,
			Margin = new Thickness(0, 18, 0, 2),
		};
		header.SetDynamicResource(Label.TextColorProperty, "ThText3");
		host.Add(header);
	}

	private static void AddNote(VerticalStackLayout host, string text)
	{
		var note = new Label { Text = text, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
		note.SetDynamicResource(Label.TextColorProperty, "ThText3");
		host.Add(note);
	}

	private static (VerticalStackLayout Texts, Label Name, Label Shows, Label Meta, Label Status) RowTexts(
		string name, string showsLine, string meta)
	{
		var texts = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
		var nameLabel = new Label { Text = name, FontSize = 16 };
		nameLabel.SetDynamicResource(Label.TextColorProperty, "ThText");
		var shows = new Label { Text = showsLine, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
		shows.SetDynamicResource(Label.TextColorProperty, "ThText3");
		var metaLabel = new Label { Text = meta, FontSize = 13 };
		metaLabel.SetDynamicResource(Label.TextColorProperty, "ThText3");
		var status = new Label { Text = "", FontSize = 13, LineBreakMode = LineBreakMode.WordWrap, IsVisible = false };
		status.SetDynamicResource(Label.TextColorProperty, "ThVerdictWarn");
		texts.Add(nameLabel);
		if (showsLine.Length > 0) texts.Add(shows);
		if (meta.Length > 0) texts.Add(metaLabel);
		texts.Add(status);
		return (texts, nameLabel, shows, metaLabel, status);
	}

	private static void AddRow(VerticalStackLayout host, VerticalStackLayout texts, View control)
	{
		var row = new Grid
		{
			ColumnDefinitions = new ColumnDefinitionCollection
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto),
			},
			Padding = new Thickness(0, 10),
			ColumnSpacing = 12,
		};
		row.Add(texts, 0, 0);
		row.Add(control, 1, 0);
		host.Add(row);
		var hair = new BoxView { HeightRequest = 0.5 };
		hair.SetDynamicResource(VisualElement.BackgroundColorProperty, "ThHairline");
		host.Add(hair);
	}

	private static Button SmallButton(string text)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			CornerRadius = 10,
			HeightRequest = 40,
			VerticalOptions = LayoutOptions.Center,
		};
		button.SetDynamicResource(Button.BackgroundColorProperty, "ThBtn2Bg");
		button.SetDynamicResource(Button.TextColorProperty, "ThAccent");
		return button;
	}

	private static void AddStaticRow(VerticalStackLayout host, string name, string showsLine, string meta)
	{
		var (texts, _, _, _, _) = RowTexts(name, showsLine, meta);
		AddRow(host, texts, new Label { Text = "✓", FontSize = 16, VerticalOptions = LayoutOptions.Center });
	}

	private static void AddPendingRow(
		VerticalStackLayout host, Store store, string id, PackIndexEntry? entry, Action? onPacksChanged)
	{
		if (entry == null)
		{
			var (texts, _, _, _, _) = RowTexts(id, "", "This universe isn't in the download list any more.");
			var forget = SmallButton("Forget it");
			forget.Clicked += (s, e) =>
			{
				store.ForgetPendingUniverse(id);
				onPacksChanged?.Invoke();
			};
			AddRow(host, texts, forget);
			return;
		}
		var (t, _, _, _, status) = RowTexts(entry.DisplayName, entry.ShowsLine, $"{entry.EpisodeCount} episodes · needs download");
		var handle = new RowHandle { Entry = entry, Status = status };
		var download = SmallButton("Download");
		handle.Action = download;
		download.Clicked += async (s, e) =>
		{
			if (_downloading) return;
			_downloading = true;
			await DownloadOneAsync(store, handle, onPacksChanged);
			_downloading = false;
			onPacksChanged?.Invoke();
		};
		AddRow(host, t, download);
	}

	private static void AddInstalledRow(
		VerticalStackLayout host, Store store, InstalledPackRecord record,
		PackIndexEntry? entry, Action? onPacksChanged,
		Func<string, string, Task<bool>>? confirmAsync)
	{
		bool update = entry != null && PackIndexParse.UpdateAvailable(entry, record);
		var meta = $"Downloaded · data version {record.DataVersion}"
			+ (update ? $" · version {entry!.DataVersion} available" : "");
		var (texts, _, _, _, status) = RowTexts(record.DisplayName, record.ShowsLine, meta);
		var buttons = new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
		if (update)
		{
			var handle = new RowHandle { Entry = entry!, Status = status };
			var updateButton = SmallButton("Update");
			handle.Action = updateButton;
			updateButton.Clicked += async (s, e) =>
			{
				if (_downloading) return;
				_downloading = true;
				await DownloadOneAsync(store, handle, onPacksChanged);
				_downloading = false;
				onPacksChanged?.Invoke();
			};
			buttons.Add(updateButton);
		}
		var remove = SmallButton("Remove");
		remove.Clicked += async (s, e) =>
		{
			if (_downloading) return;
			bool confirmed = confirmAsync == null
				|| await confirmAsync(
					$"Remove {record.DisplayName}?",
					"Its episode list is deleted from this phone. Your progress in it is kept — download it again and everything is where you left it.");
			if (!confirmed) return;
			_downloading = true;
			var result = await store.RemovePackAsync(record.Id);
			_downloading = false;
			status.Text = result.Ok
				? "Removed — your progress in it is kept, and it can be downloaded again below."
				: result.Message;
			status.IsVisible = true;
			if (result.Ok) buttons.IsEnabled = false;
			onPacksChanged?.Invoke();
		};
		buttons.Add(remove);
		AddRow(host, texts, buttons);
	}

	private static RowHandle AddAvailableRow(
		VerticalStackLayout host, Store store, PackIndexEntry entry, long? sizeBytes,
		PackSectionMode mode, HashSet<string> selected, Action refreshPickerButton, Action? onPacksChanged)
	{
		var meta = $"{entry.EpisodeCount} episodes"
			+ (sizeBytes is long size ? $" · {FormatSize(size)} download" : "");
		var (texts, _, _, _, status) = RowTexts(entry.DisplayName, entry.ShowsLine, meta);
		var handle = new RowHandle { Entry = entry, Status = status };
		if (mode == PackSectionMode.Picker)
		{
			var check = new CheckBox { VerticalOptions = LayoutOptions.Center };
			check.SetDynamicResource(CheckBox.ColorProperty, "ThAccent");
			handle.Check = check;
			check.CheckedChanged += (s, e) =>
			{
				if (e.Value) selected.Add(entry.Id);
				else selected.Remove(entry.Id);
				refreshPickerButton();
			};
			// The retry button lives under the texts (added to the
			// row's text stack) so a failed pick can be retried
			// without re-running the whole batch.
			var retry = SmallButton("Retry");
			retry.IsVisible = false;
			handle.Action = retry;
			retry.Clicked += async (s, e) =>
			{
				if (_downloading) return;
				_downloading = true;
				await DownloadOneAsync(store, handle, onPacksChanged);
				_downloading = false;
				onPacksChanged?.Invoke();
			};
			texts.Add(retry);
			AddRow(host, texts, check);
		}
		else
		{
			var download = SmallButton("Download");
			handle.Action = download;
			download.Clicked += async (s, e) =>
			{
				if (_downloading) return;
				_downloading = true;
				await DownloadOneAsync(store, handle, onPacksChanged);
				_downloading = false;
				onPacksChanged?.Invoke();
			};
			AddRow(host, texts, download);
		}
		return handle;
	}

	// ---- Download flow ------------------------------------------

	/// <summary>Download + install one pack, reporting into its
	/// own row: progress while it runs, "Installed ✓" on success
	/// (and the row's control retires), the plain failure reason
	/// plus a working Retry on failure.</summary>
	private static async Task DownloadOneAsync(Store store, RowHandle row, Action? onPacksChanged)
	{
		row.Status.Text = "Downloading…";
		row.Status.IsVisible = true;
		if (row.Action != null)
		{
			row.Action.IsEnabled = false;
			if (row.Action.Text == "Retry") row.Action.IsVisible = false;
		}
		var result = await PackService.DownloadAndInstallAsync(store, row.Entry);
		if (result.Ok)
		{
			row.Status.Text = result.WasUpdate ? "Updated ✓" : "Installed ✓";
			if (row.Action != null) row.Action.IsVisible = false;
			if (row.Check != null)
			{
				row.Check.IsEnabled = false;
				row.Check.IsChecked = true;
			}
		}
		else
		{
			row.Status.Text = result.Message;
			if (row.Action != null)
			{
				row.Action.Text = "Retry";
				row.Action.IsEnabled = true;
				row.Action.IsVisible = true;
			}
			if (row.Check != null) row.Check.IsEnabled = true;
		}
	}

	private static void RefreshDownloadSelected(Button? button, HashSet<string> selected)
	{
		if (button == null) return;
		button.Text = selected.Count == 0 ? "Download selected" : $"Download selected ({selected.Count})";
		button.IsEnabled = selected.Count > 0 && !_downloading;
	}

	private static string FormatSize(long bytes)
	{
		if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
		if (bytes >= 1024) return $"{Math.Max(1, bytes / 1024)} KB";
		return $"{bytes} B";
	}
}
