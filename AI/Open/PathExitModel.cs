using System.Collections.Concurrent;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>A leg as the path model marks it: Black-Scholes at its own IV until expiry, intrinsic after.</summary>
internal readonly record struct PathLeg(OptionParsed Parsed, decimal Iv, bool IsLong, int Qty);

/// <summary>
/// Expected P&amp;L of a position managed by take-profit / stop-loss exits that watch its mark along the way, not only at
/// the end. The former terminal-only view floored each end-of-life outcome at the stop (a free put: paths that touch the
/// stop and then recover were counted as recoveries) and ignored the take-profit entirely (paths that tag the target
/// early and then fade were counted as fades).
///
/// Method: a Markov lattice on the scoring distribution's z-nodes (<see cref="CandidateScorer.ScenarioNodes"/>, in units of
/// the total σ to the target). K monitoring steps are spaced evenly in forecast variance, so one standard transition
/// matrix — a N(0, 1/K) increment binned onto the nodes — serves every candidate and is cached per K. At each step the
/// position is marked at every node; mass whose mark is at or through a barrier exits at that mark (a check at the
/// step fills at the price it sees, so a gap through the stop realizes the gap), and the mass that survives to the
/// target is valued at the caller's end-of-life P&amp;L. Exiting at the observed mark keeps the model honest: crediting
/// the barrier level instead would hand a free put to every node found beyond the stop. Under the distribution the legs
/// are priced on, optional stopping then leaves a fair-priced position at EV ≈ 0 with or without exits.
/// </summary>
internal static class PathExitModel
{
	/// <summary>One-step transition matrix with, per row, the first and last column holding non-zero probability — the
	/// N(0, 1/K) kernel underflows to exactly 0 a few step-σ away, so the band is all the propagation has to touch.</summary>
	private sealed record Transition(double[,] P, int[] First, int[] Last);

	/// <summary>A leg's Black-Scholes inputs at one interior monitoring step — everything but the spot.</summary>
	private readonly record struct LegAtStep(double Sign, double Strike, bool IsCall, bool Expired, double Years, double Iv, double DividendPv);

	private static readonly ConcurrentDictionary<int, Transition> TransitionsBySteps = new();

	/// <summary>Monitoring steps: about four per remaining session (≈ every 100 minutes), at least 4, at most 24.</summary>
	internal static int StepCount(double sessionsRemaining) => Math.Clamp((int)Math.Ceiling(sessionsRemaining * 4.0), 4, 24);

	/// <param name="legs">Every leg of one contract-unit of the position.</param>
	/// <param name="entryValuePerContract">Net premium paid at entry per contract: debit positive, credit negative. P&amp;L = 100 × Σ signed leg values − this.</param>
	/// <param name="pnlAtTarget">End-of-life P&amp;L per contract at a target-date spot (the scorer's own terminal valuation).</param>
	/// <param name="spot">Underlying now.</param>
	/// <param name="targetCenter">z = 0 price at the target (<see cref="CandidateScorer.ScenarioCenter"/>).</param>
	/// <param name="sigmaTotal">Total log-σ from now to the target (σ_scenario·√years).</param>
	/// <param name="stepTimes">K clock times, ascending, the last being the target; evenly spaced in variance.</param>
	/// <param name="takeProfit">P&amp;L per contract (&gt; 0) that triggers the take-profit, or null when not armed.</param>
	/// <param name="stopLoss">Loss per contract (&gt; 0) that triggers the stop, or null when not armed.</param>
	internal static decimal ExpectedPnl(IReadOnlyList<PathLeg> legs, decimal entryValuePerContract, Func<decimal, decimal> pnlAtTarget, decimal spot, double targetCenter, double sigmaTotal, IReadOnlyList<DateTime> stepTimes, decimal? takeProfit, decimal? stopLoss, IReadOnlyList<DividendEvent>? dividends)
	{
		var nodes = CandidateScorer.ScenarioNodes;
		var n = nodes.Length;
		var steps = stepTimes.Count;
		var transition = TransitionsBySteps.GetOrAdd(steps, BuildTransition);
		var tp = takeProfit.HasValue ? (double)takeProfit.Value : double.PositiveInfinity;
		var sl = stopLoss.HasValue ? -(double)stopLoss.Value : double.NegativeInfinity;
		var entry = (double)entryValuePerContract;
		var legSteps = new LegAtStep[legs.Count];

		var mass = new double[n];
		mass[n / 2] = 1.0;
		var next = new double[n];
		double ev = 0;
		var logSpot = Math.Log((double)spot);
		var logDrift = Math.Log(targetCenter) - logSpot;

		for (var k = 1; k <= steps; k++)
		{
			Array.Clear(next);
			for (var i = 0; i < n; i++)
			{
				var m = mass[i];
				if (m == 0) continue;
				for (var j = transition.First[i]; j <= transition.Last[i]; j++) next[j] += m * transition.P[i, j];
			}
			(mass, next) = (next, mass);

			if (k < steps)
				for (var l = 0; l < legs.Count; l++) legSteps[l] = AtStep(legs[l], stepTimes[k - 1], dividends);

			// Node prices at step k: the z-lattice is in total-σ units; the center slides from spot to the target center.
			var center = Math.Exp(logSpot + logDrift * k / steps);
			for (var j = 0; j < n; j++)
			{
				if (mass[j] < 1e-12) continue;
				var s = center * Math.Exp(nodes[j].Z * sigmaTotal);
				var pnl = k == steps ? (double)pnlAtTarget((decimal)s) : PositionValue(legSteps, s) * 100.0 - entry;
				if (k == steps || pnl >= tp || pnl <= sl)
				{
					ev += mass[j] * pnl;
					mass[j] = 0;
				}
			}
		}
		return (decimal)ev;
	}

	/// <summary>Per-share net value of the position at spot <paramref name="s"/>: Black-Scholes at each leg's own IV (on the
	/// escrowed-dividend spot, as <see cref="OptionMath.LegValueAt"/>), intrinsic once the leg has expired.</summary>
	private static double PositionValue(LegAtStep[] legs, double s)
	{
		double value = 0;
		foreach (var leg in legs)
		{
			double legValue;
			if (leg.Expired)
				legValue = leg.IsCall ? Math.Max(0, s - leg.Strike) : Math.Max(0, leg.Strike - s);
			else
			{
				var adjusted = s - leg.DividendPv;
				legValue = OptionMath.BlackScholesValue(adjusted > 0 ? adjusted : s, leg.Strike, leg.Years, OptionMath.RiskFreeRate, leg.Iv, leg.IsCall);
			}
			value += leg.Sign * legValue;
		}
		return value;
	}

	private static LegAtStep AtStep(PathLeg leg, DateTime at, IReadOnlyList<DividendEvent>? dividends)
	{
		var expiry = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
		var sign = (leg.IsLong ? 1.0 : -1.0) * leg.Qty;
		var isCall = leg.Parsed.CallPut == "C";
		if (at >= expiry) return new LegAtStep(sign, (double)leg.Parsed.Strike, isCall, Expired: true, 0, 0, 0);
		return new LegAtStep(sign, (double)leg.Parsed.Strike, isCall, Expired: false, (expiry - at).TotalDays / 365.0, (double)leg.Iv, (double)OptionMath.DividendPresentValue(dividends, at, expiry, OptionMath.RiskFreeRate));
	}

	/// <summary>Row i = probability of moving from node i to each node in one step of variance 1/K (z units), binned on the
	/// same edges as the scoring nodes (outer bins open-ended, so every row sums to 1).</summary>
	private static Transition BuildTransition(int steps)
	{
		var nodes = CandidateScorer.ScenarioNodes;
		var n = nodes.Length;
		var width = 2.0 * CandidateScorer.ScenarioBinEdgeSigmas / n;
		var stepSd = Math.Sqrt(1.0 / steps);
		var m = new double[n, n];
		var first = new int[n];
		var last = new int[n];
		for (var i = 0; i < n; i++)
		{
			first[i] = n;
			last[i] = -1;
			for (var j = 0; j < n; j++)
			{
				var lo = j == 0 ? double.NegativeInfinity : -CandidateScorer.ScenarioBinEdgeSigmas + j * width;
				var hi = j == n - 1 ? double.PositiveInfinity : -CandidateScorer.ScenarioBinEdgeSigmas + (j + 1) * width;
				m[i, j] = OptionMath.NormalCdf((hi - nodes[i].Z) / stepSd) - OptionMath.NormalCdf((lo - nodes[i].Z) / stepSd);
				if (m[i, j] == 0) continue;
				first[i] = Math.Min(first[i], j);
				last[i] = j;
			}
		}
		return new Transition(m, first, last);
	}
}
