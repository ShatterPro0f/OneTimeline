using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>One app theme: a full colour palette plus small feel
/// flags. Layout never changes between themes — colour, type flavour
/// and shadow treatment only.</summary>
public sealed class ThemeDef
{
	public required string Id { get; init; }
	public required string Name { get; init; }
	public required IReadOnlyDictionary<string, Color> Colors { get; init; }

	/// <summary>Font family for display titles (page title, hero title,
	/// detail headline). Null = the app default.</summary>
	public string? DisplayFont { get; init; }

	/// <summary>Episode rows/details colour their show label with the
	/// show's own colour instead of the accent (One Chicago theme).</summary>
	public bool UseShowColors { get; init; }

	/// <summary>Cards use hairline borders only, no drop shadows
	/// (Futuristic theme).</summary>
	public bool FlatShadows { get; init; }

	/// <summary>Three preview colours shown in the Settings picker.</summary>
	public required Color[] Swatches { get; init; }

	/// <summary>Per-show label colours, used when UseShowColors.</summary>
	public IReadOnlyDictionary<string, Color>? ShowColors { get; init; }
}

/// <summary>
/// The theme engine. A theme is swapped into the app's merged resource
/// dictionaries under "Th*" keys; every page binds those keys with
/// DynamicResource, so switching themes recolours the whole app live,
/// no restart. The choice persists in the Store (ThemeId); the default
/// is Light Basic — the original shipping look, unchanged.
///
/// Selection vs. resolution (iteration 9): the SELECTED id
/// (SelectedId) is either a real theme id or the pseudo-id "system"
/// ("Match system"). The RESOLVED theme (Current) is always a real
/// palette: "system" resolves through the phone's own light/dark mode
/// — light → Light Basic, dark → Dark Basic — and re-resolves live if
/// the phone's mode changes while the app runs. Nothing else about a
/// theme changes: layout, fonts and behaviour are identical.
/// </summary>
public static class ThemeService
{
	private static Dictionary<string, Color> Pal(params (string Key, string Hex)[] entries)
	{
		var d = new Dictionary<string, Color>();
		foreach (var (key, hex) in entries) d[key] = Color.FromArgb(hex);
		return d;
	}

	public static ThemeDef Light { get; } = new()
	{
		Id = "light",
		Name = "Light Basic",
		Swatches = new[] { Color.FromArgb("#FFFFFF"), Color.FromArgb("#007AFF"), Color.FromArgb("#34C759") },
		Colors = Pal(
			("PageBg", "#FFFFFF"), ("CardBg", "#FBFBFD"), ("CardStroke", "#ECECF0"),
			("Text", "#1C1C1E"), ("Body", "#3A3A3C"), ("Text2", "#6E6E73"), ("Text3", "#8E8E93"),
			("Accent", "#007AFF"), ("AccentText", "#FFFFFF"),
			("FieldBg", "#F2F2F7"), ("Btn2Bg", "#EDEDF0"), ("Btn2Text", "#1C1C1E"), ("Btn2AccentText", "#007AFF"),
			("Hairline", "#EDEDF0"), ("Check", "#34C759"), ("WatchedText", "#A1A1A6"), ("CurrentBg", "#EFF6FF"),
			("PillBg", "#FFE9EC"), ("PillText", "#C81E3A"),
			("BannerBg", "#EAF7ED"), ("BannerText", "#1C5C2A"),
			("ProgressTrack", "#E5E5EA"), ("Chevron", "#C7C7CC"),
			("Stripe1", "#FBFBFD"), ("Stripe2", "#FBFBFD"), ("Stripe3", "#FBFBFD"),
			("VerdictOk", "#1F7A33"), ("VerdictWarn", "#A15C00")),
	};

	public static ThemeDef Dark { get; } = new()
	{
		Id = "dark",
		Name = "Dark Basic",
		Swatches = new[] { Color.FromArgb("#000000"), Color.FromArgb("#0A84FF"), Color.FromArgb("#30D158") },
		Colors = Pal(
			("PageBg", "#000000"), ("CardBg", "#1C1C1E"), ("CardStroke", "#2C2C2E"),
			("Text", "#FFFFFF"), ("Body", "#D1D1D6"), ("Text2", "#AEAEB2"), ("Text3", "#8E8E93"),
			("Accent", "#0A84FF"), ("AccentText", "#FFFFFF"),
			("FieldBg", "#1C1C1E"), ("Btn2Bg", "#2C2C2E"), ("Btn2Text", "#FFFFFF"), ("Btn2AccentText", "#0A84FF"),
			("Hairline", "#2C2C2E"), ("Check", "#30D158"), ("WatchedText", "#636366"), ("CurrentBg", "#0B2B4B"),
			("PillBg", "#3D0E1A"), ("PillText", "#FF8FA3"),
			("BannerBg", "#0E2E17"), ("BannerText", "#7BD88F"),
			("ProgressTrack", "#2C2C2E"), ("Chevron", "#48484A"),
			("Stripe1", "#1C1C1E"), ("Stripe2", "#1C1C1E"), ("Stripe3", "#1C1C1E"),
			("VerdictOk", "#30D158"), ("VerdictWarn", "#FFB340")),
	};

	public static ThemeDef Retro { get; } = new()
	{
		Id = "retro",
		Name = "Retro",
		DisplayFont = "serif",
		Swatches = new[] { Color.FromArgb("#F5EFE0"), Color.FromArgb("#B9551D"), Color.FromArgb("#D9A404") },
		Colors = Pal(
			("PageBg", "#F5EFE0"), ("CardBg", "#FBF7EA"), ("CardStroke", "#D8C9A3"),
			("Text", "#3B2F23"), ("Body", "#57462F"), ("Text2", "#6B5D45"), ("Text3", "#97876A"),
			("Accent", "#B9551D"), ("AccentText", "#FFF8EA"),
			("FieldBg", "#EFE6CC"), ("Btn2Bg", "#EFE6CC"), ("Btn2Text", "#3B2F23"), ("Btn2AccentText", "#B9551D"),
			("Hairline", "#DCCFA8"), ("Check", "#5F7A2A"), ("WatchedText", "#B3A586"), ("CurrentBg", "#F0E3C0"),
			("PillBg", "#E9D8A6"), ("PillText", "#7A4A00"),
			("BannerBg", "#E6E2C8"), ("BannerText", "#4C5A1E"),
			("ProgressTrack", "#E3D5AC"), ("Chevron", "#B7A67E"),
			("Stripe1", "#FBF7EA"), ("Stripe2", "#FBF7EA"), ("Stripe3", "#FBF7EA"),
			("VerdictOk", "#5F7A2A"), ("VerdictWarn", "#9A6206")),
	};

	public static ThemeDef Futuristic { get; } = new()
	{
		Id = "future",
		Name = "Futuristic",
		FlatShadows = true,
		Swatches = new[] { Color.FromArgb("#050914"), Color.FromArgb("#22D3EE"), Color.FromArgb("#8B5CF6") },
		Colors = Pal(
			("PageBg", "#050914"), ("CardBg", "#0A1224"), ("CardStroke", "#1E3A5F"),
			("Text", "#E6F4FF"), ("Body", "#C7DFF2"), ("Text2", "#9FB8CC"), ("Text3", "#5F7A94"),
			("Accent", "#22D3EE"), ("AccentText", "#03252E"),
			("FieldBg", "#0A1224"), ("Btn2Bg", "#101D33"), ("Btn2Text", "#E6F4FF"), ("Btn2AccentText", "#22D3EE"),
			("Hairline", "#1E3A5F"), ("Check", "#4ADE80"), ("WatchedText", "#46617A"), ("CurrentBg", "#0B2B3D"),
			("PillBg", "#2E1065"), ("PillText", "#C4B5FD"),
			("BannerBg", "#062E22"), ("BannerText", "#6EE7B7"),
			("ProgressTrack", "#13273F"), ("Chevron", "#33506E"),
			("Stripe1", "#0A1224"), ("Stripe2", "#0A1224"), ("Stripe3", "#0A1224"),
			("VerdictOk", "#4ADE80"), ("VerdictWarn", "#FBBF24")),
	};

	public static ThemeDef OneChicagoTheme { get; } = new()
	{
		Id = "chicago",
		Name = "One Chicago",
		UseShowColors = true,
		Swatches = new[] { Color.FromArgb("#D92B20"), Color.FromArgb("#2E6BE6"), Color.FromArgb("#17A398") },
		ShowColors = Pal(
			("Fire", "#FF7A6B"), ("PD", "#6FA8F5"), ("Med", "#3ADBC8"),
			("Justice", "#B79CED"), ("FBI", "#E3C565"), ("Law & Order: SVU", "#A7B3C2")),
		Colors = Pal(
			("PageBg", "#14171B"), ("CardBg", "#1E2228"), ("CardStroke", "#2E333B"),
			("Text", "#FFFFFF"), ("Body", "#D7DBE0"), ("Text2", "#B8BEC7"), ("Text3", "#7D8590"),
			("Accent", "#D92B20"), ("AccentText", "#FFFFFF"),
			("FieldBg", "#23272E"), ("Btn2Bg", "#2A2F36"), ("Btn2Text", "#FFFFFF"), ("Btn2AccentText", "#6FA8F5"),
			("Hairline", "#2E333B"), ("Check", "#86A98F"), ("WatchedText", "#5F666E"), ("CurrentBg", "#232B36"),
			("PillBg", "#3D1D22"), ("PillText", "#FF9D8A"),
			("BannerBg", "#1E2B22"), ("BannerText", "#9CC5A5"),
			("ProgressTrack", "#2E333B"), ("Chevron", "#4A5158"),
			("Stripe1", "#D92B20"), ("Stripe2", "#2E6BE6"), ("Stripe3", "#17A398"),
			("VerdictOk", "#9CC5A5"), ("VerdictWarn", "#E3C565")),
	};

	public static IReadOnlyList<ThemeDef> All { get; } = new[]
	{
		Light, Dark, Retro, Futuristic, OneChicagoTheme,
	};

	public static ThemeDef Current { get; private set; } = Light;

	public static event Action? ThemeChanged;

	public static ThemeDef ById(string? id)
		=> All.FirstOrDefault(t => t.Id == id) ?? Light;

	// ---- "Match system" selection (iteration 9) ----------------------

	/// <summary>The pseudo-theme id persisted in the Store when Sam
	/// picks "Match system" in Settings. It is a SELECTION, never a
	/// palette: <see cref="Current"/> always holds the resolved
	/// ThemeDef (Light Basic or Dark Basic).</summary>
	public const string SystemId = "system";

	/// <summary>What Sam actually selected: a theme id, or "system".
	/// Canonicalized by <see cref="ApplySelection"/> (unknown ids
	/// become Light Basic's id, exactly as ById falls back).</summary>
	public static string SelectedId { get; private set; } = Light.Id;

	public static bool IsSystemSelected => SelectedId == SystemId;

	/// <summary>What "system" means right now: the phone in dark mode
	/// → Dark Basic; light mode (and Unspecified, for platforms that
	/// don't report one) → Light Basic.</summary>
	public static ThemeDef ResolveSystem()
	{
		var requested = Application.Current?.RequestedTheme ?? AppTheme.Unspecified;
		return requested == AppTheme.Dark ? Dark : Light;
	}

	private static ThemeDef ResolveSelection(string? selectedId)
		=> selectedId == SystemId ? ResolveSystem() : ById(selectedId);

	/// <summary>Select a theme (or "system") and apply the resolved
	/// palette. This is the one entry point every selection change
	/// goes through — Settings taps, startup restore, progress
	/// import, start over — so the selected id and the applied
	/// palette can never drift apart.</summary>
	public static void ApplySelection(string? selectedId)
	{
		SelectedId = selectedId == SystemId ? SystemId : ById(selectedId).Id;
		Apply(ResolveSelection(SelectedId));
	}

	/// <summary>The phone's light/dark mode changed (wired to
	/// Application.RequestedThemeChanged in App). In system mode,
	/// re-resolve and swap the palette live; an explicit theme
	/// selection ignores the phone entirely.</summary>
	public static void OnSystemThemeChanged()
	{
		if (!IsSystemSelected) return;
		var resolved = ResolveSystem();
		if (resolved.Id != Current.Id) Apply(resolved);
	}

	/// <summary>Palette lookup for code-built UI.</summary>
	public static Color ColorOf(string key) => Current.Colors[key];

	/// <summary>The colour for an episode's show label: the show's own
	/// colour in show-colour themes, the accent everywhere else.</summary>
	public static Color ShowLabelColor(Episode ep)
	{
		if (Current.UseShowColors && Current.ShowColors is { } map
			&& map.TryGetValue(ep.Show, out var c))
			return c;
		return Current.UseShowColors ? ColorOf("Text3") : ColorOf("Accent");
	}

	private static ResourceDictionary? _applied;

	/// <summary>Swap the theme's palette into the app's resources.
	/// Every DynamicResource "Th*" binding in the app updates live.</summary>
	public static void Apply(ThemeDef theme)
	{
		Current = theme;
		if (Application.Current is { } app)
		{
			var dict = new ResourceDictionary();
			foreach (var kv in theme.Colors)
				dict["Th" + kv.Key] = kv.Value;

			var merged = app.Resources.MergedDictionaries;
			if (_applied != null) merged.Remove(_applied);
			merged.Add(dict);
			_applied = dict;
		}
		ThemeChanged?.Invoke();
	}
}
