using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// The universe rows shared by setup's Universes step and the
/// Settings Universes section (Decision 1C): one row per
/// catalogued universe — name, shows line, episode count, and an
/// Include switch bound straight to the Store's one selection
/// state, so the two surfaces can never disagree. The active
/// universe's row says so. Turning off the last included universe
/// is refused by the Store; the switch snaps back on rebuild and
/// the note label explains why, in plain words.
/// </summary>
public static class UniverseSection
{
	// Reentrancy guard: rebuilding replaces the rows (and their
	// switches) wholesale, and a toggle handler triggers a rebuild;
	// overlapping builds would interleave rows.
	private static bool _building;

	public static async Task BuildAsync(VerticalStackLayout host, Label? noteLabel, Action? onSelectionChanged = null)
	{
		if (_building) return;
		_building = true;
		try
		{
			host.Clear();
			var store = AppHost.Store;
			foreach (var universe in UniverseCatalogue.All)
			{
				int count = await store.UniverseEpisodeCountAsync(universe.Id);
				bool included = store.IncludedUniverseIds.Contains(universe.Id);
				bool active = store.ActiveUniverseId == universe.Id;

				var texts = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
				var name = new Label { Text = universe.DisplayName, FontSize = 16 };
				name.SetDynamicResource(Label.TextColorProperty, "ThText");
				var shows = new Label { Text = universe.ShowsLine, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };
				shows.SetDynamicResource(Label.TextColorProperty, "ThText3");
				var meta = new Label
				{
					Text = $"{count} episodes" + (active ? " · on screen now" : ""),
					FontSize = 13,
				};
				meta.SetDynamicResource(Label.TextColorProperty, "ThText3");
				texts.Add(name);
				texts.Add(shows);
				texts.Add(meta);

				var sw = new Switch { IsToggled = included, VerticalOptions = LayoutOptions.Center };
				var captured = universe;
				sw.Toggled += async (s, e) =>
				{
					if (_building) return;
					bool ok = await store.SetUniverseIncludedAsync(captured.Id, e.Value);
					if (noteLabel != null)
					{
						if (!ok)
						{
							noteLabel.Text = "At least one universe has to stay included — the app needs a watch order to track.";
							noteLabel.IsVisible = true;
						}
						else
						{
							noteLabel.IsVisible = false;
						}
					}
					onSelectionChanged?.Invoke();
					await BuildAsync(host, noteLabel, onSelectionChanged);
				};

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
				row.Add(sw, 1, 0);
				host.Add(row);
				var hair = new BoxView { HeightRequest = 0.5 };
				hair.SetDynamicResource(VisualElement.BackgroundColorProperty, "ThHairline");
				host.Add(hair);
			}
		}
		finally
		{
			_building = false;
		}
	}
}
