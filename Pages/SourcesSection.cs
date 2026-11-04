using Microsoft.Maui.ApplicationModel.DataTransfer;
using OneChicago.Services;

namespace OneChicago.Pages;

/// <summary>
/// The "Episode links" section — ONE control shared by Setup and
/// Settings (built once, placed on both), for the user's own
/// episode-links source: the bulk counterpart to the per-episode
/// "Your Prime Video link" editor.
///
/// The launch universe ships with its links included, so this is
/// the path for ADDITIONAL files — a future universe's, or the
/// user's own updated one. The app fetches only on the user's
/// explicit Fetch tap (SourceLinksService): the whole file is
/// validated first and swapped in atomically, links stay on the
/// device, and Clear removes the address and the layer while
/// leaving the shipped links and hand-saved overrides untouched.
///
/// The recommended-source block renders SourcesNote.
/// RecommendedSourceText verbatim with a Copy button — today a
/// format example; when a real hosted file exists, Sam swaps that
/// one constant and this section shows its address instead.
/// </summary>
public class SourcesSection : ContentView
{
	private readonly Entry _urlEntry;
	private readonly Button _fetchButton;
	private readonly Button _clearButton;
	private readonly Label _statusLabel;
	private bool _fetching;

	public SourcesSection()
	{
		var stack = new VerticalStackLayout { Spacing = 8 };

		var header = new Label
		{
			Text = "Episode links",
			FontSize = 12,
			FontAttributes = FontAttributes.Bold,
			CharacterSpacing = 1.2,
			Margin = new Thickness(0, 26, 0, 2),
		};
		header.SetDynamicResource(Label.TextColorProperty, "ThText3");
		stack.Add(header);

		var note = new Label
		{
			FontSize = 14,
			LineBreakMode = LineBreakMode.WordWrap,
			Text = "The One Chicago universe comes with its episode links included, so Play and hands-free switching work out of the box. This field is for additional episode-links files — a future universe, or your own updated file. Paste the address of an episode-links file and tap Fetch, and the app loads those links on top. The file is JSON: a \"links\" list pairing each episode's key with its Prime Video link or link ID. Links you load stay on this device.",
		};
		note.SetDynamicResource(Label.TextColorProperty, "ThText2");
		stack.Add(note);

		var recommendedHeader = new Label { Text = "Recommended source:", FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
		recommendedHeader.SetDynamicResource(Label.TextColorProperty, "ThText3");
		stack.Add(recommendedHeader);

		var snippet = new Label
		{
			Text = SourcesNote.RecommendedSourceText,
			FontSize = 12,
			FontFamily = "Monospace",
			LineBreakMode = LineBreakMode.WordWrap,
		};
		snippet.SetDynamicResource(Label.TextColorProperty, "ThText2");
		var snippetBorder = new Border
		{
			StrokeThickness = 0,
			Padding = new Thickness(10, 8),
			Content = snippet,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
		};
		snippetBorder.SetDynamicResource(Border.BackgroundColorProperty, "ThFieldBg");
		stack.Add(snippetBorder);

		var copyButton = new Button
		{
			Text = "Copy",
			FontSize = 15,
			CornerRadius = 12,
			HeightRequest = 40,
		};
		copyButton.SetDynamicResource(Button.BackgroundColorProperty, "ThBtn2Bg");
		copyButton.SetDynamicResource(Button.TextColorProperty, "ThAccent");
		copyButton.Clicked += OnCopy;
		stack.Add(copyButton);

		_urlEntry = new Entry
		{
			Placeholder = "https://…/onetimeline-prime-links.json",
			FontSize = 16,
			Keyboard = Keyboard.Url,
			BackgroundColor = Colors.Transparent,
			ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
		};
		_urlEntry.SetDynamicResource(Entry.TextColorProperty, "ThText");
		_urlEntry.SetDynamicResource(Entry.PlaceholderColorProperty, "ThText3");
		var entryBorder = new Border
		{
			StrokeThickness = 0,
			Padding = new Thickness(10, 0),
			Content = _urlEntry,
			StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
		};
		entryBorder.SetDynamicResource(Border.BackgroundColorProperty, "ThFieldBg");
		stack.Add(entryBorder);

		_fetchButton = new Button
		{
			Text = "Fetch",
			FontSize = 16,
			CornerRadius = 12,
			HeightRequest = 48,
		};
		_fetchButton.SetDynamicResource(Button.BackgroundColorProperty, "ThAccent");
		_fetchButton.SetDynamicResource(Button.TextColorProperty, "ThAccentText");
		_fetchButton.Clicked += OnFetch;

		_clearButton = new Button
		{
			Text = "Clear",
			FontSize = 16,
			CornerRadius = 12,
			HeightRequest = 48,
		};
		_clearButton.SetDynamicResource(Button.BackgroundColorProperty, "ThBtn2Bg");
		_clearButton.SetDynamicResource(Button.TextColorProperty, "ThAccent");
		_clearButton.Clicked += OnClear;

		var buttons = new Grid
		{
			ColumnDefinitions = new ColumnDefinitionCollection
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
			},
			ColumnSpacing = 8,
		};
		buttons.Add(_fetchButton, 0, 0);
		buttons.Add(_clearButton, 1, 0);
		stack.Add(buttons);

		_statusLabel = new Label { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
		_statusLabel.SetDynamicResource(Label.TextColorProperty, "ThText2");
		stack.Add(_statusLabel);

		Content = stack;
		Refresh();
	}

	/// <summary>Reflect the Store's current source state: the saved
	/// address prefilled in the field, and the status line rebuilt
	/// from the layer metadata. Called by both host pages whenever
	/// they appear (after AppHost.Ready, like their own loads).</summary>
	public void Refresh()
	{
		var store = AppHost.Store;
		if (!store.Loaded) return;
		_urlEntry.Text = store.SourceLinksUrl;
		_statusLabel.Text = StatusText();
	}

	private static string StatusText()
	{
		var store = AppHost.Store;
		if (store.HasSourceLinks && store.SourceLinksFetchedAt is { } fetched)
		{
			var text = $"✓ Loaded: {store.SourceLinksAppliedCount} episode links · fetched {fetched.LocalDateTime:MMM d, yyyy}";
			if (store.SourceLinksIgnoredCount > 0)
				text += $" {store.SourceLinksIgnoredCount} {(store.SourceLinksIgnoredCount == 1 ? "entry was" : "entries were")} skipped — they didn't match an episode here.";
			return text;
		}
		if (store.SourceLinksUrl.Length > 0)
			return "Address saved — tap Fetch to load the links.";
		return "Not set.";
	}

	private async void OnCopy(object? sender, EventArgs e)
	{
		await Clipboard.Default.SetTextAsync(SourcesNote.RecommendedSourceText);
		_statusLabel.Text = "Copied ✓ — paste it wherever you need it.";
	}

	private async void OnFetch(object? sender, EventArgs e)
	{
		if (_fetching) return;
		_fetching = true;
		_fetchButton.IsEnabled = false;
		_clearButton.IsEnabled = false;
		_statusLabel.Text = "Fetching…";
		try
		{
			var result = await AppHost.LinkSources.FetchAsync(_urlEntry.Text ?? "");
			_statusLabel.Text = result.Ok ? result.Message : $"✗ {result.Message}";
			if (result.Ok) _urlEntry.Text = AppHost.Store.SourceLinksUrl;
		}
		finally
		{
			_fetching = false;
			_fetchButton.IsEnabled = true;
			_clearButton.IsEnabled = true;
		}
	}

	private async void OnClear(object? sender, EventArgs e)
	{
		var store = AppHost.Store;
		if (!store.HasSourceLinks && store.SourceLinksUrl.Length == 0 && (_urlEntry.Text ?? "").Trim().Length == 0)
		{
			_statusLabel.Text = "Not set.";
			return;
		}
		var page = ParentPage();
		bool yes = page == null || await page.DisplayAlert("Remove episode links?",
			"The episode-links address and the links downloaded from it will be removed from this phone. The One Chicago links that came with the app stay, and so do any links you saved on individual episodes.",
			"Remove", "Cancel");
		if (!yes) return;
		store.ClearSourceLinks();
		_urlEntry.Text = "";
		_statusLabel.Text = "Not set.";
	}

	private ContentPage? ParentPage()
	{
		Element? el = this;
		while (el != null && el is not ContentPage) el = el.Parent;
		return el as ContentPage;
	}
}
