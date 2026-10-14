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
	private static readonly ConcurrentDictionary<int, double[,]> TransitionsBySteps = new();

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
	internal static decimal ExpectedPnl(IReadOnlyList<PathLeg> legs, decimal entryValuePerContract, Func<decimal, decimal> pnlAtTarget, decimal spot, double targetCenter, double sigmaTotal, IReadOnlyList<DateTime> stepTimes, decimal? takeProfit, decimal? stopLoss, IReadOnlyList<DividendEvent>? dividends, OptionMath.BlackScholesEvaluator? blackScholes = null)
	{
		var nodes = CandidateScorer.ScenarioNodes;
		var n = nodes.Length;
		var steps = stepTimes.Count;
		var transition = TransitionsBySteps.GetOrAdd(steps, BuildTransition);
		var tp = takeProfit.HasValue ? (double)takeProfit.Value : double.PositiveInfinity;
		var sl = stopLoss.HasValue ? -(double)stopLoss.Value : double.NegativeInfinity;

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
				if (mass[i] == 0) continue;
				for (var j = 0; j < n; j++) next[j] += mass[i] * transition[i, j];
			}
			(mass, next) = (next, mass);

			// Node prices at step k: the z-lattice is in total-σ units; the center slides from spot to the target center.
			var center = Math.Exp(logSpot + logDrift * k / steps);
			for (var j = 0; j < n; j++)
			{
				if (mass[j] < 1e-12) continue;
				var s = (decimal)(center * Math.Exp(nodes[j].Z * sigmaTotal));
				double pnl;
				if (k == steps)
					pnl = (double)pnlAtTarget(s);
				else
				{
					decimal value = 0m;
					foreach (var leg in legs)
						value += (leg.IsLong ? 1m : -1m) * leg.Qty * OptionMath.LegValueAt(s, stepTimes[k - 1], leg.Parsed, leg.Iv, dividends, blackScholes);
					pnl = (double)(value * 100m - entryValuePerContract);
				}
				if (k == steps || pnl >= tp || pnl <= sl)
				{
					ev += mass[j] * pnl;
					mass[j] = 0;
				}
			}
		}
		return (decimal)ev;
	}

	/// <summary>Row i = probability of moving from node i to each node in one step of variance 1/K (z units), binned on the
	/// same edges as the scoring nodes (outer bins open-ended, so every row sums to 1).</summary>
	private static double[,] BuildTransition(int steps)
	{
		var nodes = CandidateScorer.ScenarioNodes;
		var n = nodes.Length;
		var width = 2.0 * CandidateScorer.ScenarioBinEdgeSigmas / n;
		var stepSd = Math.Sqrt(1.0 / steps);
		var m = new double[n, n];
		for (var i = 0; i < n; i++)
		{
			for (var j = 0; j < n; j++)
			{
				var lo = j == 0 ? double.NegativeInfinity : -CandidateScorer.ScenarioBinEdgeSigmas + j * width;
				var hi = j == n - 1 ? double.PositiveInfinity : -CandidateScorer.ScenarioBinEdgeSigmas + (j + 1) * width;
				m[i, j] = OptionMath.NormalCdf((hi - nodes[i].Z) / stepSd) - OptionMath.NormalCdf((lo - nodes[i].Z) / stepSd);
			}
		}
		return m;
	}
}
