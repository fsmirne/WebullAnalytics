namespace WebullAnalytics.AI.Open.ZeroDte;

/// <summary>Applies a <see cref="ZeroDteGateVerdict"/> to individual candidates. Split in two because the
/// two checks need different inputs and belong at different points in the pipeline:
/// <list type="bullet">
///   <item><description><see cref="Admits"/> is strike-only (structure, side, placement, distance) and runs
///     BEFORE scoring, so a session the gate has not endorsed costs nothing to enumerate.</description></item>
///   <item><description><see cref="AdmitsCredit"/> needs the priced net credit and so runs AFTER scoring.</description></item>
/// </list>
/// Both fail CLOSED when the gate is enabled: a candidate is admitted only if the session read positively
/// endorses it. Neither ever admits something <c>opener.structures</c> had disabled — the gate is applied as
/// an intersection, never a union.</summary>
internal static class ZeroDteCandidateFilter
{
	/// <summary>Pre-scoring admission. <paramref name="reason"/> is set only on rejection.</summary>
	/// <param name="quotes">The option book the candidate will be priced from — used to locate the GEX wall
	/// at the candidate's own expiry when <c>placement.levelSource</c> reads walls.</param>
	public static bool Admits(
		ZeroDteGateConfig cfg,
		ZeroDteGateVerdict verdict,
		CandidateSkeleton skeleton,
		decimal spot,
		DateTime asOf,
		IReadOnlyDictionary<string, OptionContractQuote> quotes,
		out string reason)
	{
		reason = string.Empty;
		if (!cfg.Enabled) return true;

		// Scope: same-day expiries only, by default. A longer-dated candidate is outside the framework's
		// remit and passes through so this gate can be layered onto a mixed-DTE config without side effects.
		var isZeroDte = skeleton.TargetExpiry.Date == asOf.Date;
		if (cfg.ZeroDteOnly && !isZeroDte) return true;

		if (!verdict.AllowsEntry)
		{
			reason = $"session gate: {verdict.Summary}";
			return false;
		}

		if (!verdict.AllowedStructures.Contains(skeleton.StructureKind))
		{
			reason = $"session gate: {verdict.State} endorses {string.Join("/", verdict.AllowedStructures)}, not {skeleton.StructureKind}";
			return false;
		}

		if (spot <= 0m) { reason = "session gate: no spot"; return false; }

		// Every SHORT leg must be legally placed. An iron condor therefore has to satisfy the rule on both
		// wings — which is the framework's own position ("I structure both sides using the same logic").
		var gex = CandidateScorer.ComputeGex(skeleton.Ticker, skeleton.TargetExpiry, spot, asOf, quotes);
		foreach (var leg in skeleton.Legs)
		{
			if (!IsShort(leg)) continue;
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed == null) continue;
			var isCall = string.Equals(parsed.CallPut, "C", StringComparison.OrdinalIgnoreCase);
			var strike = parsed.Strike;

			// Distance bounds (Class #05 §12 "distance vs premium").
			var distPct = Math.Abs(strike - spot) / spot * 100m;
			if (cfg.Placement.MinShortDistancePct > 0m && distPct < cfg.Placement.MinShortDistancePct)
			{
				reason = $"session gate: short {strike} only {distPct:F2}% from spot, min {cfg.Placement.MinShortDistancePct}%";
				return false;
			}
			if (cfg.Placement.MaxShortDistancePct > 0m && distPct > cfg.Placement.MaxShortDistancePct)
			{
				reason = $"session gate: short {strike} is {distPct:F2}% from spot, max {cfg.Placement.MaxShortDistancePct}%";
				return false;
			}

			if (!TryProtectiveLevel(cfg, verdict, gex, isCall, spot, out var level, out var levelDesc))
			{
				if (cfg.Placement.RequireProtectiveLevel)
				{
					reason = $"session gate: no {(isCall ? "resistance" : "support")} level located for the short {(isCall ? "call" : "put")} ({levelDesc})";
					return false;
				}
				continue;
			}

			// "If there is no good short strike, there is no good trade" — a level so far out that the
			// protected strike collects nothing is not a placement, it is a pass.
			var levelDistPct = Math.Abs(level - spot) / spot * 100m;
			if (cfg.Placement.MaxLevelDistancePct > 0m && levelDistPct > cfg.Placement.MaxLevelDistancePct)
			{
				reason = $"session gate: {levelDesc} {level} is {levelDistPct:F2}% from spot, past max {cfg.Placement.MaxLevelDistancePct}% — nowhere logical to place the short";
				return false;
			}

			if (cfg.Placement.ShortBeyondLevel)
			{
				var beyond = isCall ? strike >= level : strike <= level;
				if (!beyond)
				{
					reason = $"session gate: short {(isCall ? "call" : "put")} {strike} is inside {levelDesc} {level} — not protected";
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>Post-scoring credit check (Class #03 §7 / Class #04 §8). Rejects debit structures outright —
	/// the framework trades credit only.</summary>
	public static bool AdmitsCredit(ZeroDteGateConfig cfg, OpenProposal proposal, DateTime asOf, out string reason)
	{
		reason = string.Empty;
		if (!cfg.Enabled) return true;
		if (cfg.ZeroDteOnly && proposal.Legs.Count > 0 && !AnyLegExpiresOn(proposal, asOf)) return true;

		var credit = proposal.DebitOrCreditPerContract;
		if (credit <= 0m)
		{
			reason = "session gate: framework is credit-only, this candidate is a net debit";
			return false;
		}

		// width × 100 == credit + capital-at-risk for every defined-risk credit structure the engine builds
		// (CapitalAtRiskPerContract is width×100 − credit for short verticals and the worse side of a condor).
		// Deriving the width this way keeps the check structure-agnostic instead of re-deriving geometry
		// from the legs and getting the condor's two different wing widths wrong.
		var widthDollars = credit + proposal.CapitalAtRiskPerContract;
		if (widthDollars <= 0m) return true;
		var ratio = credit / widthDollars;

		if (cfg.Credit.MinPctOfWidth > 0m && ratio < cfg.Credit.MinPctOfWidth)
		{
			reason = $"session gate: credit ${credit / 100m:F2} is {ratio:P0} of width, below min {cfg.Credit.MinPctOfWidth:P0} — not worth the risk";
			return false;
		}
		if (cfg.Credit.MaxPctOfWidth is > 0m and < 1m && ratio > cfg.Credit.MaxPctOfWidth)
		{
			reason = $"session gate: credit ${credit / 100m:F2} is {ratio:P0} of width, above max {cfg.Credit.MaxPctOfWidth:P0} — short is too close to price";
			return false;
		}
		return true;
	}

	/// <summary>Resolves the protective level for one side per <c>placement.levelSource</c>. With
	/// <c>"both"</c> the level is whichever of the wall and the range edge is FARTHER from spot, so clearing
	/// it clears both layers.</summary>
	private static bool TryProtectiveLevel(ZeroDteGateConfig cfg, ZeroDteGateVerdict verdict, CandidateScorer.GexResult gex, bool isCall, decimal spot, out decimal level, out string description)
	{
		level = 0m;
		var src = (cfg.Placement.LevelSource ?? "gexWall").ToLowerInvariant();
		var wall = isCall ? gex.CallWallAbove : gex.PutWallBelow;
		var edge = isCall ? verdict.ProtectiveCallLevel : verdict.ProtectivePutLevel;

		switch (src)
		{
			case "rangeboundary":
				description = isCall ? "range high" : "range low";
				if (!edge.HasValue) return false;
				level = edge.Value;
				return true;

			case "both":
			{
				description = isCall ? "call wall / range high" : "put wall / range low";
				if (!wall.HasValue && !edge.HasValue) return false;
				if (!wall.HasValue) { level = edge!.Value; return true; }
				if (!edge.HasValue) { level = wall.Value; return true; }
				// Farther from spot = stricter. Above spot that is the larger strike; below, the smaller.
				level = isCall ? Math.Max(wall.Value, edge.Value) : Math.Min(wall.Value, edge.Value);
				return true;
			}

			default:
				description = isCall ? "call wall" : "put wall";
				if (!wall.HasValue) return false;
				level = wall.Value;
				return true;
		}
	}

	private static bool IsShort(ProposalLeg leg) => leg.Action.StartsWith("sell", StringComparison.OrdinalIgnoreCase);

	private static bool AnyLegExpiresOn(OpenProposal proposal, DateTime asOf)
	{
		foreach (var leg in proposal.Legs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(leg.Symbol);
			if (parsed != null && parsed.ExpiryDate.Date == asOf.Date) return true;
		}
		return false;
	}
}
