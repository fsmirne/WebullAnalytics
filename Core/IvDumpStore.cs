using System.Globalization;

namespace WebullAnalytics;

/// <summary>Single owner of the per-day vendor-chain CSV archive <c>data/iv/&lt;TICKER&gt;/&lt;ET date&gt;.csv</c>
/// (header <c>date,time,source,expiry,strike,right,bid,ask,iv,oi,spot,volume</c>): the per-strike vendor inputs —
/// bid/ask, vendor IV, OI, cumulative day volume — behind a live fetch, which exist nowhere on disk after the fact.
/// Writers: `analyze gex --dump`, the running-day `analyze gex --intraday` chain capture, and wa-scraper's per-minute
/// vendor capture; readers: the running-day --intraday heatmap's capture slices and its volume tape (per-bucket
/// Δvolume = the difference of consecutive captures' cumulative counts). Source-tagged so interleaved webull/schwab
/// rows land in one day file and join on (time, expiry, strike, right); null vendor fields dump as empty (a vendor
/// null is itself data); the time column is the actual ET fetch time, not a bar label. The volume column was added
/// 2026-08-18 — older files (and rows appended before a writer restart) carry 11 fields; readers must treat a
/// missing 12th field as volume-null, never as zero. Callers pre-filter the contracts to their own window — this
/// writer only drops symbols that fail to parse to the requested root.</summary>
internal static class IvDumpStore
{
	/// <summary>Appends one row per parseable contract, creating the day file (with header) on first write.
	/// Returns the number of rows written.</summary>
	internal static int Append(string ticker, string source, DateTime nowEt, decimal spot, IEnumerable<OptionContractQuote> contracts)
	{
		var sb = new System.Text.StringBuilder();
		var rows = 0;
		foreach (var q in contracts.OrderBy(c => c.ContractSymbol, StringComparer.Ordinal))
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
			if (parsed == null || !string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			string D(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
			sb.Append(nowEt.ToString("yyyy-MM-dd,HH:mm:ss", CultureInfo.InvariantCulture)).Append(',').Append(source).Append(',')
				.Append(parsed.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
				.Append(parsed.Strike.ToString(CultureInfo.InvariantCulture)).Append(',').Append(parsed.CallPut).Append(',')
				.Append(D(q.Bid)).Append(',').Append(D(q.Ask)).Append(',').Append(D(q.ImpliedVolatility)).Append(',')
				.Append(q.OpenInterest?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
				.Append(spot.ToString(CultureInfo.InvariantCulture)).Append(',')
				.Append(q.Volume?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');
			rows++;
		}
		if (rows == 0) return 0;
		var path = Program.ResolvePath($"data/iv/{ticker}/{nowEt:yyyy-MM-dd}.csv");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		if (!File.Exists(path)) File.WriteAllText(path, "date,time,source,expiry,strike,right,bid,ask,iv,oi,spot,volume\n");
		File.AppendAllText(path, sb.ToString());
		return rows;
	}

	/// <summary>Per-capture cumulative day volume for one ET date and source: capture time → (expiry, strike, right) → volume,
	/// keeping only rows <paramref name="include"/> accepts (by expiry and strike). Rows without a volume field (pre-2026-08-18
	/// layout) are skipped rather than read as zero. Empty when the day file does not exist.</summary>
	internal static SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>> LoadVolumeSeries(string ticker, DateTime date, string source, Func<DateTime, decimal, bool> include)
	{
		var series = new SortedDictionary<TimeSpan, Dictionary<(DateTime, decimal, string), long>>();
		var path = Program.ResolvePath($"data/iv/{ticker}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return series;
		foreach (var line in File.ReadLines(path).Skip(1))
		{
			var f = line.Split(',');
			if (f.Length < 12 || !string.Equals(f[2], source, StringComparison.OrdinalIgnoreCase)) continue;
			if (!TimeSpan.TryParseExact(f[1], @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts)) continue;
			if (!DateTime.TryParseExact(f[3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) continue;
			if (!decimal.TryParse(f[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var strike) || !include(expiry, strike)) continue;
			if (!long.TryParse(f[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)) continue;
			if (!series.TryGetValue(ts, out var capture)) series[ts] = capture = new Dictionary<(DateTime, decimal, string), long>();
			capture[(expiry, strike, f[5])] = volume;
		}
		return series;
	}
}
