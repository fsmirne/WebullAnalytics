using System.Collections.Concurrent;
using System.Text.Json;

namespace WebullAnalytics.Pricing.Clock;

/// <summary>Each root's dated clock fits, <c>data/clock/{ROOT}.fits.json</c>: a monthly history (for backtests) plus the latest daily
/// refit (for live use). A fit applies only to instants AFTER its <see cref="ClockFit.FittedThrough"/> day, so one lookup serves both:
/// live picks the newest fit, a backtest day picks the newest fit built strictly before it — never one fitted on its future.</summary>
internal static class ClockFits
{
	/// <summary>Rolling fit window, in calendar days ending at the fit's last session.</summary>
	internal const int WindowDays = 365;

	private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
	private static readonly ConcurrentDictionary<string, IReadOnlyList<ClockFit>> Cache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The root a ticker's clock is fitted and looked up under: SPX and SPXW share the SPX index's chain behaviour.</summary>
	internal static string ClockRoot(string root) => string.Equals(root, "SPX", StringComparison.OrdinalIgnoreCase) ? "SPXW" : root.ToUpperInvariant();

	private static string PathFor(string root) => Program.ResolvePath($"data/clock/{ClockRoot(root)}.fits.json");

	/// <summary>Fits for <paramref name="root"/> ordered by <see cref="ClockFit.FittedThrough"/>; empty when never fitted.</summary>
	internal static IReadOnlyList<ClockFit> Load(string root) => Cache.GetOrAdd(ClockRoot(root), static r =>
	{
		var path = PathFor(r);
		return File.Exists(path) ? (JsonSerializer.Deserialize<List<ClockFit>>(File.ReadAllText(path), Json) ?? []).OrderBy(f => f.FittedThrough).ToList() : [];
	});

	internal static void Save(string root, IReadOnlyList<ClockFit> fits)
	{
		var path = PathFor(root);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var ordered = fits.OrderBy(f => f.FittedThrough).ToList();
		File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(ordered, Json));
		File.Move(path + ".tmp", path, overwrite: true);
		Cache[ClockRoot(root)] = ordered;
	}

	/// <summary>The newest fit built strictly before <paramref name="instant"/>'s date, or null.</summary>
	internal static ClockFit? At(string root, DateTime instant)
	{
		var day = DateOnly.FromDateTime(instant);
		ClockFit? best = null;
		foreach (var fit in Load(root))
		{
			if (fit.FittedThrough >= day) break;
			best = fit;
		}
		return best;
	}

	/// <summary>Brings <paramref name="root"/>'s fit history up to date from its store: one fit per month (through the last stored session
	/// before each month starts) once a full window exists, plus one through the newest stored session. Existing fits are kept.
	/// Returns the newest fit (null when the store can't support one yet) and how many fits were added.</summary>
	internal static (ClockFit? Latest, int Added) Refresh(string root, Action<string> progress)
	{
		var dates = ClockStore.Dates(root);
		if (dates.Count == 0) return (null, 0);
		var fits = Load(root).ToList();
		var have = fits.Select(f => f.FittedThrough).ToHashSet();

		var targets = new SortedSet<DateOnly>();
		for (var month = new DateOnly(dates[0].Year, dates[0].Month, 1).AddMonths(1); month <= dates[^1]; month = month.AddMonths(1))
		{
			var through = dates.LastOrDefault(d => d < month);
			if (through != default && through.DayNumber - dates[0].DayNumber >= WindowDays - 7) targets.Add(through);
		}
		targets.Add(dates[^1]);

		var days = new Dictionary<DateOnly, ClockDay>();
		var added = 0;
		foreach (var through in targets.Where(t => !have.Contains(t)))
		{
			var window = dates.Where(d => d <= through && through.DayNumber - d.DayNumber < WindowDays).ToList();
			foreach (var d in window.Where(d => !days.ContainsKey(d)))
				if (ClockStore.Load(root, d) is { } day) days[d] = day;
			var fit = ClockFitter.Fit(ClockRoot(root), window.Where(days.ContainsKey).Select(d => days[d]).ToList());
			if (fit == null) continue;
			fits.Add(fit);
			added++;
			progress($"{ClockRoot(root)}: fitted through {through:yyyy-MM-dd} → overnight {fit.Overnight:0.00}, closed-day {fit.NonTrading:0.00}");
		}
		if (added > 0) Save(root, fits);
		var ordered = fits.OrderBy(f => f.FittedThrough).ToList();
		return (ordered.Count > 0 ? ordered[^1] : null, added);
	}
}
