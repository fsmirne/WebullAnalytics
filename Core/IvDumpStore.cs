using System.Globalization;

namespace WebullAnalytics;

/// <summary>Single owner of the per-day vendor-chain CSV archive <c>data/iv/&lt;TICKER&gt;/&lt;ET date&gt;.csv</c>
/// (header <c>date,time,source,expiry,strike,right,bid,ask,iv,oi,spot</c>): the per-strike vendor inputs — bid/ask,
/// vendor IV, OI — behind a live fetch, which exist nowhere on disk after the fact. Writers: `analyze gex --dump`,
/// the running-day `analyze gex --intraday` chain capture, and wa-scraper's per-minute vendor capture; reader: the
/// running-day --intraday heatmap's capture slices. Source-tagged so interleaved webull/schwab rows land in one day
/// file and join on (time, expiry, strike, right); null vendor fields dump as empty (a vendor null is itself data);
/// the time column is the actual ET fetch time, not a bar label. Callers pre-filter the contracts to their own
/// window — this writer only drops symbols that fail to parse to the requested root.</summary>
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
				.Append(spot.ToString(CultureInfo.InvariantCulture)).Append('\n');
			rows++;
		}
		if (rows == 0) return 0;
		var path = Program.ResolvePath($"data/iv/{ticker}/{nowEt:yyyy-MM-dd}.csv");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		if (!File.Exists(path)) File.WriteAllText(path, "date,time,source,expiry,strike,right,bid,ask,iv,oi,spot\n");
		File.AppendAllText(path, sb.ToString());
		return rows;
	}
}
