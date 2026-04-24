using System.Text.Json;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Fetches recent daily bars from Yahoo's public chart endpoint and produces a TrendSnapshot.
/// Returns null on any failure (network, non-200, malformed JSON, insufficient bars). Never throws to
/// callers — trend is designed to be optional.</summary>
internal static class TrendFetcher
{
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
			decimal? prevClose = meta.TryGetProperty("chartPreviousClose", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDecimal() : null;

			var quotes = root.GetProperty("indicators").GetProperty("quote")[0];
			var closes = ParseDecimalArray(quotes.GetProperty("close"));
			var highs = ParseDecimalArray(quotes.GetProperty("high"));
			var lows = ParseDecimalArray(quotes.GetProperty("low"));

			if (closes.Count < 21) return null;

			var idx5 = closes.Count - 1 - 5;
			var idx20 = closes.Count - 1 - 20;
			if (idx5 < 0 || idx20 < 0) return null;
			var change5 = closes[idx5] == 0m ? 0m : 100m * (spot - closes[idx5]) / closes[idx5];
			var change20 = closes[idx20] == 0m ? 0m : 100m * (spot - closes[idx20]) / closes[idx20];

			decimal atrSum = 0m;
			int atrCount = 0;
			for (int i = Math.Max(1, closes.Count - 20); i < closes.Count; i++)
			{
				var tr = Math.Max(highs[i] - lows[i], Math.Max(Math.Abs(highs[i] - closes[i - 1]), Math.Abs(lows[i] - closes[i - 1])));
				atrSum += tr;
				atrCount++;
			}
			var atr = atrCount > 0 ? atrSum / atrCount : 0m;
			var atrPct = spot > 0m ? 100m * atr / spot : 0m;

			decimal? intraday = null;
			if (prevClose is decimal pc2 && pc2 > 0m)
				intraday = 100m * (spot - pc2) / pc2;

			return new TrendSnapshot(
				ChangePctIntraday: intraday,
				ChangePct5Day: change5,
				ChangePct20Day: change20,
				Spot20DayAtrPct: atrPct,
				AsOf: asOf);
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
