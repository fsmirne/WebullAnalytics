using System.Collections.Concurrent;
using System.Globalization;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

/// <summary>Daily underlying closes for the backfill's ±band strike filter and IV back-solve (the Python backfill's
/// fetch_underlying_closes). Prefers the local <c>data/history/&lt;TICKER&gt;.csv</c> (written by <c>wa ai history</c>): free, deep,
/// no index entitlement. A local series that stops short of the window end — even by one day — must not be trusted alone: the band
/// filter drops quote days with no spot, so an evening pull whose history still ended yesterday lost the newest session (SPY
/// 2026-07-22). Only the missing tail is fetched from ThetaData (equity EOD, then index EOD) and merged, local winning where both
/// have a day.</summary>
internal sealed class UnderlyingCloses(ThetaDataClient client, BackfillLog log)
{
	// SPX (legacy AM-settled root, backfilled only for its OI) settles on the same S&P 500 level as SPXW; reuse SPXW's local history
	// instead of ThetaData's index feed, which needs a subscription this account doesn't have.
	private static readonly Dictionary<string, string> LocalAlias = new(StringComparer.OrdinalIgnoreCase) { ["SPX"] = "SPXW" };

	// Each ticker's local history parsed once per run; the history step runs before the backfill, so the file doesn't change under it.
	private readonly ConcurrentDictionary<string, Lazy<SortedDictionary<DateOnly, double>>> _local = new(StringComparer.OrdinalIgnoreCase);

	internal async Task<Dictionary<DateOnly, double>> GetAsync(string ticker, DateOnly start, DateOnly end, CancellationToken cancellation)
	{
		var local = Local(ticker, start, end);
		if ((local.Count == 0 || local.Keys.Max() < end) && LocalAlias.TryGetValue(ticker, out var alias))
			foreach (var (day, close) in Local(alias, start, end))
				local.TryAdd(day, close);   // the ticker's own history wins where present, the alias fills the rest
		if (local.Count > 0 && local.Keys.Max() >= end)
			return local;

		var tailStart = local.Count > 0 ? Max(start, local.Keys.Max().AddDays(1)) : start;
		var remote = await RemoteAsync(ticker, tailStart, end, logFailure: local.Count == 0, cancellation);
		foreach (var (day, close) in local)
			remote[day] = close;
		return remote;
	}

	private Dictionary<DateOnly, double> Local(string ticker, DateOnly start, DateOnly end) =>
		_local.GetOrAdd(ticker, t => new Lazy<SortedDictionary<DateOnly, double>>(() => ReadHistory(t))).Value
			.Where(kv => kv.Key >= start && kv.Key <= end).ToDictionary(kv => kv.Key, kv => kv.Value);

	/// <summary>date -> close from data/history/&lt;TICKER&gt;.csv, rows with a missing close skipped; empty when the file is absent.</summary>
	private static SortedDictionary<DateOnly, double> ReadHistory(string ticker)
	{
		var closes = new SortedDictionary<DateOnly, double>();
		var path = Program.ResolvePath($"data/history/{ticker.ToUpperInvariant()}.csv");
		if (!File.Exists(path)) return closes;
		using var reader = new StreamReader(path);
		var header = reader.ReadLine()?.Split(',') ?? [];
		var (dateCol, closeCol) = (Array.IndexOf(header, "date"), Array.IndexOf(header, "close"));
		if (dateCol < 0 || closeCol < 0) return closes;
		while (reader.ReadLine() is { } line)
		{
			var cells = line.Split(',');
			if (cells.Length <= Math.Max(dateCol, closeCol)) continue;
			if (DateOnly.TryParseExact(cells[dateCol], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
				&& double.TryParse(cells[closeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out var close) && !double.IsNaN(close))
				closes[day] = close;
		}
		return closes;
	}

	/// <summary>ThetaData's equity EOD, else its index EOD (a trailing-W weekly root maps to its index). Best effort: a failure
	/// leaves the tail empty (logged when there's no local series either).</summary>
	private async Task<Dictionary<DateOnly, double>> RemoteAsync(string ticker, DateOnly start, DateOnly end, bool logFailure, CancellationToken cancellation)
	{
		try
		{
			var stock = Closes(await ThetaRetry.CollectAsync(token => client.StockHistoryEod(ticker, start, end, token), cancellation));
			if (stock.Count > 0) return stock;
		}
		catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException)) { }   // not an equity — try the index feed

		try
		{
			var index = ticker.EndsWith('W') ? ticker[..^1] : ticker;
			return Closes(await ThetaRetry.CollectAsync(token => client.IndexHistoryEod(index, start, end, token), cancellation));
		}
		catch (Exception ex) when (ex is not (ThetaSessionException or OperationCanceledException))
		{
			if (logFailure) log.Info($"  [warn] underlying close fetch failed for {ticker}: {ex.Message}; underlyingPrice=null");
			return [];
		}
	}

	private static Dictionary<DateOnly, double> Closes(ThetaTable table)
	{
		var closes = new Dictionary<DateOnly, double>();
		if (table.RowCount == 0) return closes;
		var (dateCol, closeCol) = (ThetaCells.Pick(table, "date", "created", "timestamp"), ThetaCells.Pick(table, "close", "price", "last"));
		for (var row = 0; row < table.RowCount; row++)
			closes[ThetaCells.Date(table, row, dateCol)] = table.Double(row, closeCol);
		return closes;
	}

	private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
