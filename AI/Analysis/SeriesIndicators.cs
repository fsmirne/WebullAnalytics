namespace WebullAnalytics.AI.Analysis;

/// <summary>Standalone, dependency-free technical-indicator math over a close-price series, used by the
/// 5-minute dip-signal analysis (<see cref="DipSignalAnalyzer"/>). Each function returns an array aligned
/// 1:1 with the input; positions before the indicator's warm-up are null. Kept separate from
/// <c>TechnicalIndicators</c> (which emits normalized [-1,1] opener BIAS scores on daily closes) because
/// the dip analysis needs the RAW indicator values (RSI level, band price, MACD histogram) on intraday bars.</summary>
internal static class SeriesIndicators
{
	/// <summary>Exponential moving average. Seeded with the simple average of the first <paramref name="period"/>
	/// closes (placed at index period-1), then the standard EMA recurrence. Null before the seed.</summary>
	public static decimal?[] Ema(IReadOnlyList<decimal> closes, int period)
	{
		var ema = new decimal?[closes.Count];
		if (closes.Count < period || period < 1) return ema;
		var mult = 2m / (period + 1);
		decimal seed = 0m;
		for (var i = 0; i < period; i++) seed += closes[i];
		var prev = seed / period;
		ema[period - 1] = prev;
		for (var i = period; i < closes.Count; i++)
		{
			prev = (closes[i] - prev) * mult + prev;
			ema[i] = prev;
		}
		return ema;
	}

	/// <summary>Wilder's RSI. Raw 0–100 level (not normalized). Seeded with the simple average of the first
	/// <paramref name="period"/> gains/losses, then Wilder smoothing. Null until index <paramref name="period"/>.</summary>
	public static decimal?[] Rsi(IReadOnlyList<decimal> closes, int period = 14)
	{
		var rsi = new decimal?[closes.Count];
		if (closes.Count <= period || period < 1) return rsi;

		decimal gainSum = 0m, lossSum = 0m;
		for (var i = 1; i <= period; i++)
		{
			var ch = closes[i] - closes[i - 1];
			if (ch >= 0m) gainSum += ch; else lossSum -= ch;
		}
		var avgGain = gainSum / period;
		var avgLoss = lossSum / period;
		rsi[period] = RsiFrom(avgGain, avgLoss);
		for (var i = period + 1; i < closes.Count; i++)
		{
			var ch = closes[i] - closes[i - 1];
			var gain = ch > 0m ? ch : 0m;
			var loss = ch < 0m ? -ch : 0m;
			avgGain = (avgGain * (period - 1) + gain) / period;
			avgLoss = (avgLoss * (period - 1) + loss) / period;
			rsi[i] = RsiFrom(avgGain, avgLoss);
		}
		return rsi;
	}

	private static decimal RsiFrom(decimal avgGain, decimal avgLoss) =>
		avgLoss == 0m ? 100m : 100m - 100m / (1m + avgGain / avgLoss);

	/// <summary>MACD line (EMA<paramref name="fast"/> − EMA<paramref name="slow"/>), its signal line
	/// (EMA<paramref name="signal"/> of the MACD line), and the histogram (line − signal). Each aligned to the
	/// input; null during warm-up. The signal EMA is seeded only once the MACD line itself exists.</summary>
	public static (decimal?[] Line, decimal?[] Signal, decimal?[] Histogram) Macd(IReadOnlyList<decimal> closes, int fast = 12, int slow = 26, int signal = 9)
	{
		var emaFast = Ema(closes, fast);
		var emaSlow = Ema(closes, slow);
		var n = closes.Count;
		var line = new decimal?[n];
		for (var i = 0; i < n; i++)
			if (emaFast[i].HasValue && emaSlow[i].HasValue)
				line[i] = emaFast[i]!.Value - emaSlow[i]!.Value;

		// Signal = EMA(signal) of the MACD line, computed over the contiguous non-null tail of `line`.
		var signalLine = new decimal?[n];
		var firstLine = Array.FindIndex(line, v => v.HasValue);
		if (firstLine >= 0 && n - firstLine >= signal)
		{
			var mult = 2m / (signal + 1);
			decimal seed = 0m;
			for (var i = firstLine; i < firstLine + signal; i++) seed += line[i]!.Value;
			var prev = seed / signal;
			signalLine[firstLine + signal - 1] = prev;
			for (var i = firstLine + signal; i < n; i++)
			{
				prev = (line[i]!.Value - prev) * mult + prev;
				signalLine[i] = prev;
			}
		}

		var hist = new decimal?[n];
		for (var i = 0; i < n; i++)
			if (line[i].HasValue && signalLine[i].HasValue)
				hist[i] = line[i]!.Value - signalLine[i]!.Value;
		return (line, signalLine, hist);
	}

	/// <summary>Lower Bollinger Band: basis − <paramref name="k"/>×σ. See <see cref="Bollinger"/>.</summary>
	public static decimal?[] BollingerLower(IReadOnlyList<decimal> closes, int period = 20, decimal k = 2m, bool emaBasis = false)
		=> Bollinger(closes, period, k, emaBasis).Lower;

	/// <summary>Full Bollinger Bands: middle basis and ±<paramref name="k"/>×σ. σ is the population standard
	/// deviation over the window (matches TradingView <c>ta.stdev</c>, which uses the SMA mean internally
	/// regardless of the basis MA). The basis is the SMA by default, or an EMA when <paramref name="emaBasis"/>
	/// is set — the latter matches this study's source indicator (BB "Basis MA Type" = EMA). Each array aligned
	/// to the input; null until index period-1.</summary>
	public static (decimal?[] Lower, decimal?[] Mid, decimal?[] Upper) Bollinger(IReadOnlyList<decimal> closes, int period = 20, decimal k = 2m, bool emaBasis = false)
	{
		var n = closes.Count;
		var lower = new decimal?[n];
		var mid = new decimal?[n];
		var upper = new decimal?[n];
		if (n < period || period < 1) return (lower, mid, upper);
		var ema = emaBasis ? Ema(closes, period) : null;
		for (var i = period - 1; i < n; i++)
		{
			decimal sum = 0m;
			for (var j = i - period + 1; j <= i; j++) sum += closes[j];
			var sma = sum / period;
			decimal sq = 0m;
			for (var j = i - period + 1; j <= i; j++) { var d = closes[j] - sma; sq += d * d; }
			var band = k * (decimal)Math.Sqrt((double)(sq / period));
			var basis = ema is null ? sma : ema[i]!.Value;   // σ always around the SMA; basis may be EMA
			mid[i] = basis;
			lower[i] = basis - band;
			upper[i] = basis + band;
		}
		return (lower, mid, upper);
	}
}
