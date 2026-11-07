using OneChicago.Services;

namespace OneChicago;

public partial class App : Application
{
	public static bool InForeground { get; private set; } = true;

	/// <summary>True while the background-mode foreground service
	/// (iteration 6) is running. RokuMonitor polls when the app is in
	/// the foreground OR this is set — the service is what keeps the
	/// monitor, completion detection and autoplay alive off-screen.
	/// Set only by the Android service itself.</summary>
	public static bool MonitorServiceActive { get; internal set; }

	public App()
	{
		InitializeComponent();

		// "Match system" (iteration 9): if the phone's light/dark mode
		// flips while the app runs, a system selection re-resolves
		// live. ThemeService ignores the event for explicit themes.
		RequestedThemeChanged += (_, _) => ThemeService.OnSystemThemeChanged();

		// The Light Basic palette must be in the resources before the
		// first page renders (every page binds "Th*" DynamicResources).
		// AppHost swaps in the saved theme as soon as the store loads.
		ThemeService.Apply(ThemeService.Light);

		MainPage = new AppShell();

		// Load the episode dataset + saved progress, then start watching
		// the TV. Polling runs while the app is in the foreground, and —
		// since iteration 6 — whenever the background service runs.
		_ = AppHost.Ready;
	}

	protected override void OnStart()
	{
		InForeground = true;
		StartBackgroundService();
	}

	protected override void OnResume()
	{
		InForeground = true;
		StartBackgroundService();
	}

	protected override void OnSleep() => InForeground = false;

	/// <summary>Ask the platform to start the background-mode service.
	/// Called only from foreground lifecycle points, as Android's
	/// background-start restrictions require. The service itself checks
	/// the persisted Background mode toggle once the store is loaded
	/// and stops itself if the user turned it off — so this call is safe on
	/// every launch/resume.</summary>
	private static void StartBackgroundService()
	{
#if ANDROID
		Platforms.MonitorServiceController.EnsureStarted();
#endif
	}
}
