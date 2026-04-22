namespace WebullAnalytics.AI;

internal static class TechnicalIndicators
{
	/// <summary>(SMA5 / SMA20 − 1) clamped to [−1, +1]. Requires ≥ 20 closes.</summary>
	public static decimal? ComputeSmaScore(IReadOnlyList<decimal> closes)
	{
		if (closes.Count < 20) return null;
		var sma5 = closes.Skip(closes.Count - 5).Average();
		var sma20 = closes.Skip(closes.Count - 20).Average();
		if (sma20 == 0m) return null;
		return Math.Clamp(sma5 / sma20 - 1m, -1m, 1m);
	}

	/// <summary>Wilder RSI(14) normalized: (RSI − 50) / 50. Requires ≥ 15 closes (14 changes).</summary>
	public static decimal? ComputeRsiScore(IReadOnlyList<decimal> closes)
	{
		if (closes.Count < 15) return null;

		var changes = new List<decimal>(closes.Count - 1);
		for (int i = 0; i < closes.Count - 1; i++)
			changes.Add(closes[i + 1] - closes[i]);

		// Seed: simple average of first 14 gains/losses.
		var seedGain = changes.Take(14).Where(c => c > 0).DefaultIfEmpty(0m).Average();
		var seedLoss = changes.Take(14).Where(c => c < 0).Select(c => -c).DefaultIfEmpty(0m).Average();

		var avgGain = seedGain;
		var avgLoss = seedLoss;

		// Wilder smoothing on remaining changes.
		for (int i = 14; i < changes.Count; i++)
		{
			avgGain = (avgGain * 13m + Math.Max(0m, changes[i])) / 14m;
			avgLoss = (avgLoss * 13m + Math.Max(0m, -changes[i])) / 14m;
		}

		if (avgLoss == 0m) return 1m; // all gains, maximally bullish
		var rs = avgGain / avgLoss;
		var rsi = 100m - (100m / (1m + rs));
		return (rsi - 50m) / 50m;
	}

	/// <summary>N-day % return clamped to [−1, +1]. Requires ≥ days + 1 closes.</summary>
	public static decimal? ComputeMomentumScore(IReadOnlyList<decimal> closes, int days)
	{
		if (closes.Count < days + 1) return null;
		var current = closes[closes.Count - 1];
		var prior = closes[closes.Count - 1 - days];
		if (prior == 0m) return null;
		return Math.Clamp(current / prior - 1m, -1m, 1m);
	}

	/// <summary>Weighted composite of all three indicators. Returns null when no indicator has enough data.</summary>
	public static TechnicalBias? Compute(IReadOnlyList<decimal> closes, TechnicalFilterConfig config)
	{
		var smaScore = ComputeSmaScore(closes);
		var rsiScore = ComputeRsiScore(closes);
		var momentumScore = ComputeMomentumScore(closes, config.MomentumDays);

		var weightedSum = 0m;
		var totalWeight = 0m;

		if (smaScore.HasValue && config.SmaWeight > 0m) { weightedSum += smaScore.Value * config.SmaWeight; totalWeight += config.SmaWeight; }
		if (rsiScore.HasValue && config.RsiWeight > 0m) { weightedSum += rsiScore.Value * config.RsiWeight; totalWeight += config.RsiWeight; }
		if (momentumScore.HasValue && config.MomentumWeight > 0m) { weightedSum += momentumScore.Value * config.MomentumWeight; totalWeight += config.MomentumWeight; }

		if (totalWeight == 0m) return null;

		return new TechnicalBias(
			Score: weightedSum / totalWeight,
			SmaScore: smaScore ?? 0m,
			RsiScore: rsiScore ?? 0m,
			MomentumScore: momentumScore ?? 0m);
	}
}
