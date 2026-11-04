using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace OneChicago.Pages;

/// <summary>
/// The diagnostics preview (production Decision 6): the snapshot
/// text, in full, exactly as it will be shared — the user reads it
/// here first and only the Share button sends it, through the
/// Android share sheet, to wherever they choose.
/// </summary>
public partial class DiagnosticsPage : ContentPage
{
	private readonly string _snapshot;

	public DiagnosticsPage(string snapshot)
	{
		InitializeComponent();
		_snapshot = snapshot;
		SnapshotLabel.Text = snapshot;
	}

	private async void OnShare(object sender, EventArgs e)
	{
		await Share.Default.RequestAsync(new ShareTextRequest
		{
			Title = "One Timeline diagnostics",
			Text = _snapshot,
		});
	}
}
