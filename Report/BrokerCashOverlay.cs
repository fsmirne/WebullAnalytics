using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebullAnalytics.Report;

/// <summary>Overlays broker-POSTED cash from data/cashrecord.jsonl (fetched by `wa fetch`, see
/// ApiClient.FetchCashRecordToJsonl) onto parsed trades. The order export reconstructs money as
/// price×qty±fee, but its average price is quantized (split fills) and its fee field can be flat wrong
/// (XSP: ~$0.80/leg claimed vs ~$0.03 charged) — the cash record is the only source that ties to the
/// platform's book to the penny. Matching is per LEG by root+expiry+right+side+posting DATE (posted rows
/// lag fills by up to ~20 min and recent descriptions omit the strike, so time and strike can't be hard
/// keys; strike equality IS enforced when the row carries one). Supports single fills as well as split fills
/// (multiple posted rows summing to computed cash within tolerance), each posted row consumed at most once.
/// Purely offline — reads the local file only; anything unmatched (stale file, pre-history rows, settlements)
/// keeps the computed fallback.</summary>
internal static partial class BrokerCashOverlay
{
	// "Bought SPY 20260828P  740.000" / "Sold XSP 20260623C 752.000" / "Sold SPY 20260805P" (no strike)
	[GeneratedRegex(@"^(Bought|Sold)\s+(\w+)\s+(\d{8})([CP])(?:\s+([\d.]+))?\s*$")]
	private static partial Regex TradeDescRegex();

	private sealed class PostedRow
	{
		public DateTime Date;
		public string Root = "";
		public DateTime Expiry;
		public string CallPut = "";
		public Side Side;
		public decimal? Strike;
		public decimal Amount;
		public bool Used;
	}

	/// <summary>Sets <see cref="Trade.BrokerCash"/> on every option leg/standalone trade with a matching
	/// posted row (or set of split-fill rows summing to computed cash), then on each strategy parent whose legs
	/// ALL matched (parent = sum of leg postings; a partially-matched combo keeps the computed path so its cash
	/// stays internally consistent).</summary>
	internal static void Apply(List<Trade> trades, string dataDir)
	{
		var path = Path.Combine(dataDir, Path.GetFileName(Program.CashRecordPath));
		if (!File.Exists(path)) return;

		var posted = LoadPostedTradeRows(path);
		if (posted.Count == 0) return;

		var byKey = posted.GroupBy(p => (p.Root, p.Expiry.Date, p.CallPut, p.Side, p.Date.Date)).ToDictionary(g => g.Key, g => g.ToList());

		for (var i = 0; i < trades.Count; i++)
		{
			var t = trades[i];
			if (t.Asset != Asset.Option || t.Side is not (Side.Buy or Side.Sell)) continue;
			var parsed = MatchKeys.ParseOption(t.MatchKey);
			if (parsed == null) continue;
			var p = parsed.Value.parsed;

			if (!byKey.TryGetValue((p.Root, p.ExpiryDate.Date, p.CallPut, t.Side, t.Timestamp.Date), out var candidates)) continue;

			// Fee-inclusive computed cash for THIS leg — the yardstick for closest-amount matching.
			var computed = (t.Side == Side.Sell ? 1m : -1m) * t.Price * t.Qty * t.Multiplier - (t.Fee ?? 0m);
			var tolerance = Math.Max(5.0m, t.Qty * 2.0m);

			var available = candidates.Where(c => !c.Used && (c.Strike == null || c.Strike == p.Strike))
				.OrderBy(c => Math.Abs((c.Date - t.Timestamp).TotalSeconds))
				.ToList();
			if (available.Count == 0) continue;

			// 1. Single candidate match within tolerance
			var singleMatches = available
				.Where(c => Math.Abs(c.Amount - computed) <= tolerance)
				.OrderBy(c => Math.Abs(c.Amount - computed))
				.ThenBy(c => Math.Abs((c.Date - t.Timestamp).TotalSeconds))
				.ToList();

			if (singleMatches.Count > 0)
			{
				var best = singleMatches[0];
				best.Used = true;
				trades[i] = t with { BrokerCash = best.Amount };
				continue;
			}

			// 2. Split fills: check if a combination of available candidates sums to computed cash within tolerance
			var bestSubset = FindBestSubset(available, computed, tolerance, t.Timestamp);
			if (bestSubset != null)
			{
				foreach (var c in bestSubset)
					c.Used = true;
				trades[i] = t with { BrokerCash = bestSubset.Sum(c => c.Amount) };
			}
		}

		// Parents: posted cash moves at the parent row in ComputeReport, so a combo gets broker cash
		// only when every leg matched — mixing posted and computed legs inside one combo would tie neither book.
		for (var i = 0; i < trades.Count; i++)
		{
			var t = trades[i];
			if (t.Asset != Asset.OptionStrategy || t.Side is not (Side.Buy or Side.Sell)) continue;
			var legs = Trade.GetLegs(trades, t.Seq);
			if (legs.Count >= 2 && legs.All(l => l.BrokerCash.HasValue))
				trades[i] = t with { BrokerCash = legs.Sum(l => l.BrokerCash!.Value) };
		}
	}

	private static List<PostedRow> LoadPostedTradeRows(string path)
	{
		var rows = new List<PostedRow>();
		foreach (var line in File.ReadLines(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			using var doc = JsonDocument.Parse(line);
			var root = doc.RootElement;
			if (!root.TryGetProperty("name", out var name) || name.GetString() != "Trade") continue;
			if (!root.TryGetProperty("description", out var descEl) || !root.TryGetProperty("amount", out var amountEl) || !root.TryGetProperty("occurredTime", out var timeEl)) continue;

			var desc = Regex.Replace(descEl.GetString() ?? "", @"\s+", " ").Trim();
			var m = TradeDescRegex().Match(desc);
			if (!m.Success) continue;

			if (!decimal.TryParse(amountEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) continue;
			if (!ParsingHelpers.TryParseWebullDateTime(timeEl.GetString() ?? "", out var occurred)) continue;
			if (!DateTime.TryParseExact(m.Groups[3].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) continue;

			rows.Add(new PostedRow
			{
				Date = occurred,
				Root = m.Groups[2].Value,
				Expiry = expiry,
				CallPut = m.Groups[4].Value,
				Side = m.Groups[1].Value == "Bought" ? Side.Buy : Side.Sell,
				Strike = m.Groups[5].Success && decimal.TryParse(m.Groups[5].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var strike) ? strike : null,
				Amount = amount,
			});
		}
		return rows;
	}

	/// <summary>
	/// Searches for a combination of 2 or more available posted rows whose sum equals target within tolerance.
	/// Prefers subsets with smallest diff, then smallest size, then closest average time to tradeTime.
	/// </summary>
	private static List<PostedRow>? FindBestSubset(List<PostedRow> available, decimal target, decimal tolerance, DateTime tradeTime, int maxSubsetSize = 5)
	{
		List<PostedRow>? bestSubset = null;
		decimal bestDiff = decimal.MaxValue;
		double bestTimeDiff = double.MaxValue;

		void Search(int startIdx, List<PostedRow> current, decimal currentSum)
		{
			if (current.Count >= 2)
			{
				var diff = Math.Abs(currentSum - target);
				if (diff <= tolerance)
				{
					var timeDiff = current.Average(c => Math.Abs((c.Date - tradeTime).TotalSeconds));
					if (diff < bestDiff || (diff == bestDiff && (current.Count < bestSubset!.Count || (current.Count == bestSubset.Count && timeDiff < bestTimeDiff))))
					{
						bestSubset = new List<PostedRow>(current);
						bestDiff = diff;
						bestTimeDiff = timeDiff;
					}
				}
			}

			if (current.Count >= maxSubsetSize) return;

			for (var i = startIdx; i < available.Count; i++)
			{
				current.Add(available[i]);
				Search(i + 1, current, currentSum + available[i].Amount);
				current.RemoveAt(current.Count - 1);
			}
		}

		Search(0, new List<PostedRow>(), 0m);
		return bestSubset;
	}
}
