using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// The per-universe content-category switches shared by
/// setup's Universes step and the Settings Content types
/// section (Sam's ruling, 2026-11-04): for each INCLUDED
/// universe whose pack mixes more than one category of content
/// (films beside episodes, one-shots, web extras…), a block
/// naming the universe, then one row per category — display
/// name, entry count, and a switch bound straight to the
/// Store's per-universe state, so the two surfaces can never
/// disagree.
///
/// A universe with fewer than two categories gets no block,
/// and when no universe qualifies the whole section hides
/// itself (the caller passes its header and explainer labels):
/// all-episode packs like Chicago and CSI keep exactly the
/// settings surface they always had. Switching a category off
/// is non-destructive — progress is kept and returns with the
/// switch, the universe-exclusion precedent.
/// </summary>
public static class CategorySection
{
	// Reentrancy guard, same as UniverseSection's: rebuilding
	// replaces the rows wholesale, and a toggle can trigger a
	// rebuild through the universe rows' callback.
	private static bool _building;

	public static async Task BuildAsync(VerticalStackLayout host, Label? headerLabel, Label? explainerLabel)
	{
		if (_building) return;
		_building = true;
		try
		{
			host.Clear();
			var store = AppHost.Store;
			bool anyShown = false;
			foreach (var universe in UniverseCatalogue.All)
			{
				if (!store.IncludedUniverseIds.Contains(universe.Id)) continue;
				var categories = await store.UniverseCategoriesAsync(universe.Id);
				if (categories.Count < 2) continue;
				anyShown = true;

				var uName = new Label
				{
					Text = universe.DisplayName,
					FontSize = 15,
					FontAttributes = FontAttributes.Bold,
					Margin = new Thickness(0, 12, 0, 0),
				};
				uName.SetDynamicResource(Label.TextColorProperty, "ThText");
				host.Add(uName);

				foreach (var (category, count) in categories)
				{
					var texts = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
					var name = new Label { Text = EpisodeCategories.DisplayName(category), FontSize = 16 };
					name.SetDynamicResource(Label.TextColorProperty, "ThText");
					var meta = new Label { Text = $"{count} entries", FontSize = 13 };
					meta.SetDynamicResource(Label.TextColorProperty, "ThText3");
					texts.Add(name);
					texts.Add(meta);

					var sw = new Switch
					{
						IsToggled = !store.CategoryHidden(universe.Id, category),
						VerticalOptions = LayoutOptions.Center,
					};
					var capturedUniverse = universe.Id;
					var capturedCategory = category;
					sw.Toggled += (s, e) =>
					{
						if (_building) return;
						store.SetCategoryHidden(capturedUniverse, capturedCategory, !e.Value);
					};

					var row = new Grid
					{
						ColumnDefinitions = new ColumnDefinitionCollection
						{
							new ColumnDefinition(GridLength.Star),
							new ColumnDefinition(GridLength.Auto),
						},
						Padding = new Thickness(0, 8),
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
			if (headerLabel != null) headerLabel.IsVisible = anyShown;
			if (explainerLabel != null) explainerLabel.IsVisible = anyShown;
			host.IsVisible = anyShown;
		}
		finally
		{
			_building = false;
		}
	}
}
