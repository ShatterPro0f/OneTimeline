using OneChicago.Models;

namespace OneChicago.Services;

/// <summary>
/// Runtime totals for the tracker header (Decision 1M): the header
/// reports TIME, not episode counts — the total runtime of the
/// active universe, how much of it has been watched, and the
/// watched share. The scope is exactly the scope the old count
/// line used: every entry in the active universe, watched =
/// checked off (Episode.Watched). An entry with no runtime value
/// contributes 0 minutes — runtimes are never estimated or
/// invented. Kept as a pure function of the episode list so the
/// tracker page and any checker share one calculation.
/// </summary>
public static class RuntimeStats
{
	public static (int TotalMinutes, int WatchedMinutes) Sum(IEnumerable<Episode> episodes)
	{
		int total = 0, watched = 0;
		foreach (var e in episodes)
		{
			int r = e.RuntimeMinutes ?? 0;
			total += r;
			if (e.Watched) watched += r;
		}
		return (total, watched);
	}

	/// <summary>Watched share of the total runtime, whole percent,
	/// rounded the way the old count percentage was.</summary>
	public static int Percent(int watchedMinutes, int totalMinutes) =>
		totalMinutes > 0 ? (int)Math.Round(100.0 * watchedMinutes / totalMinutes) : 0;

	/// <summary>The progress-bar fraction for the same figures.</summary>
	public static double Fraction(int watchedMinutes, int totalMinutes) =>
		totalMinutes > 0 ? (double)watchedMinutes / totalMinutes : 0;

	/// <summary>A minute count in days/hours/minutes form, exact
	/// to the minute: "24d 15h 6m", "3h 12m", "45m". Leading units
	/// drop out only when zero; the minutes are always shown, so
	/// the figure is never rounded.</summary>
	public static string Format(int minutes)
	{
		int d = minutes / 1440, h = minutes % 1440 / 60, m = minutes % 60;
		if (d > 0) return $"{d}d {h}h {m}m";
		if (h > 0) return $"{h}h {m}m";
		return $"{m}m";
	}
}
