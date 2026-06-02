using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI.Analysis;

/// <summary>Per-(strike-mode, horizon) P&L of buying a call on each dip signal. Returns are on PREMIUM
/// (a frac: 0.20 = +20% of the premium paid). Net subtracts a flat round-trip spread drag.</summary>
internal sealed record CallStats(string StrikeMode, string Horizon, int N, double WinRate, decimal AvgNet, decimal MedianNet, decimal AvgGross, decimal AvgPremium);

/// <summary>Per-short-strike-mode stats for a 0DTE bull put spread held to the cash settle. Credit/MaxLoss in
/// index points; AvgPnl in $ per spread; returns-on-risk are net P&L / max loss (fraction).</summary>
internal sealed record PutSpreadStats(string ShortMode, int N, double WinRate, decimal AvgCredit, decimal AvgMaxLoss, decimal AvgPnl, decimal AvgRoR, decimal MedianRoR, double StoppedRate);

/// <summary>Simulates buying a call (ATM or ~0.25-delta) on each dip signal and exiting at +30m / +60m / EOD,
/// priced with Black–Scholes off the REAL underlying move using a per-day VIX1D implied vol. THIS IS A MODEL
/// ESTIMATE, NOT A REAL-DATA BACKTEST — we have no real intraday SPXW option quotes, and IV is held constant
/// over the hold (which flatters a long call: in reality IV is elevated at the dip you buy and falls on the
/// bounce, an adverse vega path). Treat the output as an upper-ish bound, not a tradeable result.</summary>
internal static class DipOptionsOverlay
{
	/// <summary>Net return on premium of a long call from (s0,t0) to (s1,t1) at strike k, constant iv.
	/// <paramref name="roundTripSpread"/> is a flat fraction of premium subtracted for entry+exit bid/ask.
	/// Null if the entry premium is non-positive (degenerate).</summary>
	public static decimal? CallReturnOnPremium(decimal s0, decimal s1, decimal k, double t0, double t1, double r, decimal iv, decimal roundTripSpread)
	{
		var p0 = OptionMath.BlackScholes(s0, k, t0, r, iv, "C");
		if (p0 <= 0m) return null;
		var p1 = OptionMath.BlackScholes(s1, k, Math.Max(0.0, t1), r, iv, "C");
		return (p1 - p0) / p0 - roundTripSpread;
	}

	/// <summary>ATM = nearest $5 strike to spot. ~0.25-delta = walk OTM in $5 steps until the call delta first
	/// drops to ≤ 0.25, then keep whichever of the last two strikes is closer to 0.25.</summary>
	public static decimal AtmStrike(decimal spot) => Math.Round(spot / 5m) * 5m;

	public static decimal Delta25Strike(decimal spot, double t, double r, decimal iv)
	{
		var k = AtmStrike(spot);
		var prevK = k;
		var prevD = OptionMath.Delta(spot, k, t, r, iv, "C");
		for (var step = 0; step < 400; step++)
		{
			var d = OptionMath.Delta(spot, k, t, r, iv, "C");
			if (d <= 0.25m)
				return Math.Abs(d - 0.25m) < Math.Abs(prevD - 0.25m) ? k : prevK;
			prevK = k; prevD = d; k += 5m;
		}
		return k;
	}

	/// <summary>The OTM short-put strike whose |delta| ≈ <paramref name="target"/>: walk down in $5 steps from
	/// ATM until |put delta| first drops to ≤ target, keeping whichever of the last two strikes is closer.</summary>
	public static decimal ShortPutStrikeForDelta(decimal spot, double t, double r, decimal iv, decimal target)
	{
		var k = AtmStrike(spot);
		var prevK = k;
		var prevD = Math.Abs(OptionMath.Delta(spot, k, t, r, iv, "P"));
		for (var step = 0; step < 400 && k > 5m; step++)
		{
			var d = Math.Abs(OptionMath.Delta(spot, k, t, r, iv, "P"));
			if (d <= target)
				return Math.Abs(d - target) < Math.Abs(prevD - target) ? k : prevK;
			prevK = k; prevD = d; k -= 5m;
		}
		return k;
	}

	/// <summary>Simulates SELLING a 0DTE bull put spread (short put at strike per mode, long put <paramref
	/// name="width"/> below) on each dip signal and holding to the cash settle (SPXW is cash-settled, so the
	/// exit is deterministic intrinsic at the session close — NO exit-IV assumption needed, which is why this is
	/// far more trustworthy than the long-call overlay). Entry credit is BS-priced with flat VIX1D vol (real put
	/// skew would add credit, so this is CONSERVATIVE for the seller). Returns are on capital-at-risk (max loss).</summary>
	/// <summary>Per-strike implied vol with a linear put skew: IV rises by <paramref name="skewPtsPerPct"/> vol
	/// points for each 1% the strike sits below spot (equity put skew). 0 = flat. NOTE this cuts BOTH ways on a
	/// bull put spread's credit — you collect more on the (less-OTM) short but pay more for the (more-OTM) long,
	/// so the net effect on credit is not obviously favorable; the run shows which dominates.</summary>
	private static decimal SkewedIv(decimal baseIv, decimal spot, decimal k, decimal skewPtsPerPct)
	{
		if (spot <= 0m || skewPtsPerPct == 0m) return baseIv;
		var pctBelowAtm = (spot - k) / spot * 100m; // > 0 for OTM puts (k < spot)
		var iv = baseIv + skewPtsPerPct / 100m * pctBelowAtm;
		return iv < 0.001m ? 0.001m : iv;
	}

	public static IReadOnlyList<PutSpreadStats> RunPutSpreads(IReadOnlyList<IntradayBar> bars, IReadOnlyList<DipSignal> signals, Func<DateTime, decimal?> ivForDate, double r, decimal width, decimal frictionPts, decimal stopMultiple, decimal skewPtsPerPct)
	{
		var modes = new (string Name, decimal Delta)[] { ("ATM", 0m), ("0.30Δ", 0.30m), ("0.15Δ", 0.15m) };
		var ror = new Dictionary<string, List<decimal>>();
		var pnl = new Dictionary<string, List<decimal>>();
		var credits = new Dictionary<string, List<decimal>>();
		var maxlosses = new Dictionary<string, List<decimal>>();
		var stops = new Dictionary<string, int>();
		foreach (var m in modes) { ror[m.Name] = new(); pnl[m.Name] = new(); credits[m.Name] = new(); maxlosses[m.Name] = new(); stops[m.Name] = 0; }

		var idxByEt = new Dictionary<DateTime, int>();
		for (var i = 0; i < bars.Count; i++) idxByEt[bars[i].EtStart] = i;

		foreach (var sig in signals)
		{
			if (ivForDate(sig.EntryEt.Date) is not { } ivPct || ivPct <= 0m) continue;
			if (!idxByEt.TryGetValue(sig.EntryEt, out var e)) continue;
			var iv = ivPct / 100m;
			var expiry = sig.EntryEt.Date.AddHours(16); // 0DTE: today's 16:00 ET
			var t0 = (expiry - sig.EntryEt).TotalDays / 365.0;
			if (t0 <= 0) continue;
			var sessionEnd = e;
			while (sessionEnd + 1 < bars.Count && bars[sessionEnd + 1].Day == sig.EntryEt.Date) sessionEnd++;

			foreach (var (name, delta) in modes)
			{
				var ks = name == "ATM" ? AtmStrike(sig.EntryPrice) : ShortPutStrikeForDelta(sig.EntryPrice, t0, r, iv, delta);
				var kl = ks - width;
				if (kl <= 0m) continue;
				var credit = OptionMath.BlackScholes(sig.EntryPrice, ks, t0, r, SkewedIv(iv, sig.EntryPrice, ks, skewPtsPerPct), "P")
					- OptionMath.BlackScholes(sig.EntryPrice, kl, t0, r, SkewedIv(iv, sig.EntryPrice, kl, skewPtsPerPct), "P");
				if (credit <= 0m) continue;
				var maxLoss = width - credit;
				if (maxLoss <= 0m) continue;

				var (net, stopped) = ExitNet(bars, e, sessionEnd, expiry, ks, kl, width, credit, iv, r, frictionPts, stopMultiple, skewPtsPerPct);
				if (stopped) stops[name]++;
				credits[name].Add(credit);
				maxlosses[name].Add(maxLoss);
				pnl[name].Add(net * 100m);                            // per contract ($100 multiplier)
				ror[name].Add(net / maxLoss);
			}
		}

		var results = new List<PutSpreadStats>();
		foreach (var (name, _) in modes)
		{
			var rs = ror[name];
			if (rs.Count == 0) { results.Add(new PutSpreadStats(name, 0, 0, 0, 0, 0, 0, 0, 0)); continue; }
			var sorted = rs.OrderBy(x => x).ToList();
			results.Add(new PutSpreadStats(name, rs.Count,
				(double)rs.Count(x => x > 0m) / rs.Count,
				credits[name].Average(), maxlosses[name].Average(),
				pnl[name].Average(), rs.Average(), sorted[sorted.Count / 2],
				(double)stops[name] / rs.Count));
		}
		return results;
	}

	/// <summary>P&L per share of the spread on a single signal: if a stop is set, walk the 5-min path from entry
	/// to the close repricing the spread (BS, entry IV held constant — a real IV spike on a drop would worsen
	/// the mark, so the modeled stop is approximate) and cut the moment the buy-back mark reaches
	/// credit·(1+stop). If never hit (or no stop), settle to intrinsic at the close. Returns whether it stopped.</summary>
	private static (decimal net, bool stopped) ExitNet(IReadOnlyList<IntradayBar> bars, int entryIdx, int sessionEnd, DateTime expiry, decimal ks, decimal kl, decimal width, decimal credit, decimal iv, double r, decimal frictionPts, decimal stopMultiple, decimal skewPtsPerPct)
	{
		if (stopMultiple > 0m)
		{
			var stopMark = credit * (1m + stopMultiple); // buy-back cost at which loss = stopMultiple × credit
			for (var j = entryIdx; j < sessionEnd; j++)
			{
				var tj = (expiry - bars[j].EtStart).TotalDays / 365.0;
				if (tj <= 0) break;
				var mark = OptionMath.BlackScholes(bars[j].Close, ks, tj, r, SkewedIv(iv, bars[j].Close, ks, skewPtsPerPct), "P")
					- OptionMath.BlackScholes(bars[j].Close, kl, tj, r, SkewedIv(iv, bars[j].Close, kl, skewPtsPerPct), "P");
				mark = Math.Clamp(mark, 0m, width);
				if (mark >= stopMark) return (credit - mark - frictionPts, true);
			}
		}
		var liab = Math.Clamp(ks - bars[sessionEnd].Close, 0m, width); // intrinsic settle
		return (credit - liab - frictionPts, false);
	}

	public static IReadOnlyList<CallStats> Run(IReadOnlyList<DipSignal> signals, Func<DateTime, decimal?> ivForDate, Func<DateTime, DateTime> nextTradingDay, double r, decimal roundTripSpread)
	{
		// strikeMode → horizon → (gross, net) returns
		var modes = new[] { "ATM", "0.25Δ" };
		var horizons = new[] { "+30m", "+60m", "EOD" };
		var net = new Dictionary<(string, string), List<decimal>>();
		var gross = new Dictionary<(string, string), List<decimal>>();
		var prem = new Dictionary<string, List<decimal>>();
		foreach (var m in modes) { prem[m] = new(); foreach (var h in horizons) { net[(m, h)] = new(); gross[(m, h)] = new(); } }

		foreach (var sig in signals)
		{
			if (ivForDate(sig.EntryEt.Date) is not { } ivPct || ivPct <= 0m) continue;
			var iv = ivPct / 100m;
			var expiry = nextTradingDay(sig.EntryEt.Date).Date.AddHours(16); // 1DTE expiry, 16:00 ET
			var t0 = (expiry - sig.EntryEt).TotalDays / 365.0;
			if (t0 <= 0) continue;

			foreach (var m in modes)
			{
				var k = m == "ATM" ? AtmStrike(sig.EntryPrice) : Delta25Strike(sig.EntryPrice, t0, r, iv);
				prem[m].Add(OptionMath.BlackScholes(sig.EntryPrice, k, t0, r, iv, "C"));
				foreach (var (h, ret) in new[] { ("+30m", sig.Ret30), ("+60m", sig.Ret60), ("EOD", (decimal?)sig.RetEod) })
				{
					if (ret is not { } und) continue;
					var exitTime = h == "EOD" ? sig.EntryEt.Date.AddHours(16) : sig.EntryEt.AddMinutes(h == "+30m" ? 30 : 60);
					var t1 = (expiry - exitTime).TotalDays / 365.0;
					var s1 = sig.EntryPrice * (1m + und);
					if (CallReturnOnPremium(sig.EntryPrice, s1, k, t0, t1, r, iv, roundTripSpread) is not { } netRet) continue;
					net[(m, h)].Add(netRet);
					gross[(m, h)].Add(netRet + roundTripSpread);
				}
			}
		}

		var results = new List<CallStats>();
		foreach (var m in modes)
			foreach (var h in horizons)
			{
				var ns = net[(m, h)];
				if (ns.Count == 0) { results.Add(new CallStats(m, h, 0, 0, 0, 0, 0, 0)); continue; }
				var sorted = ns.OrderBy(x => x).ToList();
				results.Add(new CallStats(m, h, ns.Count,
					(double)ns.Count(x => x > 0m) / ns.Count,
					ns.Average(), sorted[sorted.Count / 2], gross[(m, h)].Average(),
					prem[m].Count > 0 ? prem[m].Average() : 0m));
			}
		return results;
	}
}
