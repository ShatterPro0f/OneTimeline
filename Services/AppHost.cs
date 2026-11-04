namespace OneChicago.Services;

/// <summary>Simple service host: one Store, one Roku client, one monitor,
/// one guided player, one autoplay coordinator.</summary>
public static class AppHost
{
	public static Store Store { get; } = new();
	public static RokuClient Roku { get; } = new(Store);
	public static RokuMonitor Monitor { get; } = new(Store, Roku);
	public static GuidedPlayer Player { get; } = new(Store, Roku);
	public static AutoplayCoordinator Autoplay { get; } = new(Store, Monitor, Player);
	public static DataRefreshService Refresher { get; } = new(Store);

	private static Task? _ready;

	public static Task Ready => _ready ??= InitAsync();

	private static async Task InitAsync()
	{
		await Store.LoadAsync();
		// Apply Sam's saved theme selection (App applied Light Basic at
		// startup so the first frame was never unstyled). The selection
		// may be "system" (iteration 9), which resolves through the
		// phone's current light/dark mode — so compare the selection
		// itself, not just the resolved palette, before skipping.
		bool selectionDiffers = ThemeService.SelectedId != Store.ThemeId
			|| (Store.ThemeId == ThemeService.SystemId
				&& ThemeService.ResolveSystem().Id != ThemeService.Current.Id);
		if (selectionDiffers)
			MainThread.BeginInvokeOnMainThread(() => ThemeService.ApplySelection(Store.ThemeId));
		Monitor.Start();
		// Data refresh (iteration 7): re-pull the dataset from its
		// sources on every cold start. Fire-and-forget — it runs in
		// the background, never blocks first paint, and a failure
		// leaves the loaded dataset exactly as it is.
		_ = Refresher.RefreshOnStartAsync();
	}
}
