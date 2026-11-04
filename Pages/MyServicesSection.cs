using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// The My Services rows shared by setup and the Settings
/// section of the same name (Sam's ruling, 2026-11-04): one row
/// per streaming platform in the catalogue — its display name
/// and an Include switch bound straight to the Store's one
/// selection state, so the two surfaces can never disagree.
///
/// Turning a service off never deletes anything: the packs'
/// links stay in the data, and episode pages still badge every
/// link an entry carries. What changes is playback — Watch only
/// ever uses a service the user has (Store.WatchLinksFor), so
/// an entry that only lives on services the user doesn't have
/// becomes a pure checklist entry until the service is picked
/// again.
/// </summary>
public static class MyServicesSection
{
	// Reentrancy guard, same as UniverseSection's: a toggle
	// writes the Store, and a rebuild must never interleave.
	private static bool _building;

	public static void Build(VerticalStackLayout host)
	{
		if (_building) return;
		_building = true;
		try
		{
			host.Clear();
			var store = AppHost.Store;
			foreach (var platform in PlatformCatalog.All)
			{
				var name = new Label
				{
					Text = platform.DisplayName,
					FontSize = 16,
					VerticalOptions = LayoutOptions.Center,
				};
				name.SetDynamicResource(Label.TextColorProperty, "ThText");

				var sw = new Switch
				{
					IsToggled = store.HasService(platform.Signifier),
					VerticalOptions = LayoutOptions.Center,
				};
				var captured = platform;
				sw.Toggled += (s, e) =>
				{
					if (_building) return;
					store.SetServiceSelected(captured.Signifier, e.Value);
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
				row.Add(name, 0, 0);
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
