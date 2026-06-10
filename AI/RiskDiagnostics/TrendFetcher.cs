using System.Text.Json;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Fetches recent daily bars from Yahoo's public chart endpoint and produces a TrendSnapshot.
/// Returns null on any failure (network, non-200, malformed JSON, insufficient bars). Never throws to
/// callers — trend is designed to be optional.</summary>
internal static class TrendFetcher
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	internal static async Task<TrendSnapshot?> FetchAsync(string ticker, DateTime asOf, CancellationToken ct)
	{
		try
		{
			var period2 = new DateTimeOffset(asOf.Date.AddDays(1)).ToUnixTimeSeconds();
			var period1 = new DateTimeOffset(asOf.Date.AddDays(-50)).ToUnixTimeSeconds();
			var url = $"https://query2.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?period1={period1}&period2={period2}&interval=1d&events=history";
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.UserAgent.ParseAdd("Mozilla/5.0 WebullAnalytics/1.0");
			using var resp = await Http.SendAsync(req, ct);
			if (!resp.IsSuccessStatusCode) return null;
			var body = await resp.Content.ReadAsStringAsync(ct);
			return ParseSnapshot(body, asOf);
		}
		catch
		{
			return null;
		}
	}

	internal static TrendSnapshot? ParseSnapshot(string json, DateTime asOf)
	{
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
			var meta = root.GetProperty("meta");
			var spot = meta.GetProperty("regularMarketPrice").GetDecimal();

			var quotes = root.GetProperty("indicators").GetProperty("quote")[0];
			var closes = ParseDecimalArray(quotes.GetProperty("close"));
			var highs = ParseDecimalArray(quotes.GetProperty("high"));
			var lows = ParseDecimalArray(quotes.GetProperty("low"));

			if (closes.Count < 21) return null;

			// Prior completed session = the last bar dated strictly BEFORE asOf (NY date). Positional
			// count−2 is only correct while the market is open (last bar = today's forming bar); run
			// pre-market or after hours, the last bar is already the most recent COMPLETED session and
			// count−2 lands one session stale — Monday's pivots showing on Wednesday pre-market. Do NOT
			// use meta.chartPreviousClose either — that's the close before the FIRST bar in the range,
			// ~51 trading days ago. Falls back to count−2 when timestamps are absent or misaligned.
			var prior = closes.Count - 2;
			DateTime? priorNyDate = null;
			if (root.TryGetProperty("timestamp", out var tsArr) && tsArr.ValueKind == JsonValueKind.Array && tsArr.GetArrayLength() == closes.Count)
			{
				for (var i = closes.Count - 1; i >= 0; i--)
				{
					var nyDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(tsArr[i].GetInt64()), NyTz).Date;
					if (nyDate < asOf.Date) { prior = i; priorNyDate = nyDate; break; }
				}
			}

			// Overnight, Yahoo's quote.close (and adjclose) for the just-finished session is null until
			// some later refresh — only meta.regularMarketPrice carries the close, stamped 16:00 of that
			// session via regularMarketTime. When the date-selected prior bar is missing its close and
			// the meta stamp is the SAME session date, regularMarketPrice IS that session's close.
			var priorCloseVal = prior >= 0 ? closes[prior] : 0m;
			if (priorCloseVal <= 0m && priorNyDate is DateTime pd
				&& meta.TryGetProperty("regularMarketTime", out var rmt) && rmt.ValueKind == JsonValueKind.Number
				&& TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(rmt.GetInt64()), NyTz).Date == pd)
				priorCloseVal = spot;

			decimal? prevClose = priorCloseVal > 0m ? priorCloseVal : (decimal?)null;

			var idx5 = closes.Count - 1 - 5;
			var idx20 = closes.Count - 1 - 20;
			if (idx5 < 0 || idx20 < 0) return null;
			var change5 = closes[idx5] == 0m ? 0m : 100m * (spot - closes[idx5]) / closes[idx5];
			var change20 = closes[idx20] == 0m ? 0m : 100m * (spot - closes[idx20]) / closes[idx20];

			// 14-period Wilder ATR to match broker displays (Webull, TradingView, most terminals).
			// Seed with SMA of the first 14 TR values, then apply Wilder smoothing:
			//   ATR[i] = ((N−1) × ATR[i−1] + TR[i]) / N    // equivalent to EMA with alpha = 1/N.
			const int atrPeriod = 14;
			decimal atr = 0m;
			if (closes.Count > atrPeriod)
			{
				decimal seed = 0m;
				for (int i = 1; i <= atrPeriod; i++)
					seed += Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
				atr = seed / atrPeriod;
				for (int i = atrPeriod + 1; i < closes.Count; i++)
				{
					var tr = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
					atr = ((atrPeriod - 1) * atr + tr) / atrPeriod;
				}
			}
			var atrPct = spot > 0m ? 100m * atr / spot : 0m;

			decimal? intraday = null;
			if (prevClose is decimal pc2 && pc2 > 0m)
				intraday = 100m * (spot - pc2) / pc2;

			// Prior session H/L/C from the same date-selected bar as prevClose (close may have come from
			// the meta fallback above; H/L are populated in the quote arrays even overnight).
			var hasPrior = prior >= 0 && highs[prior] > 0m && lows[prior] > 0m && priorCloseVal > 0m;

			return new TrendSnapshot(
				ChangePctIntraday: intraday,
				ChangePct5Day: change5,
				ChangePct20Day: change20,
				Atr14Pct: atrPct,
				AsOf: asOf,
				PriorHigh: hasPrior ? highs[prior] : null,
				PriorLow: hasPrior ? lows[prior] : null,
				PriorClose: hasPrior ? priorCloseVal : null);
		}
		catch
		{
			return null;
		}
	}

	private static List<decimal> ParseDecimalArray(JsonElement arr)
	{
		var list = new List<decimal>(arr.GetArrayLength());
		foreach (var e in arr.EnumerateArray())
			list.Add(e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : 0m);
		return list;
	}
}
