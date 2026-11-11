using System.Collections.Concurrent;
using System.Globalization;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Open.ZeroDte;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.AI.Sources;
using WebullAnalytics.Api;
using WebullAnalytics.Pricing;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Market movement classification produced by <see cref="CascadeScreener"/>:
/// <list type="bullet">
///   <item><description><see cref="BearCascade"/> — Dealer short-gamma + put-flow + intraday VIX expansion + VWAP breakdown continuation.</description></item>
///   <item><description><see cref="BearTrap"/> — Initial bearish stretch (gap-down / morning dip) where intraday VIX stalls/drops and price reclaims VWAP.</description></item>
///   <item><description><see cref="BullCascade"/> — Intraday VIX crush + call-flow + overhead target wall + VWAP breakout continuation.</description></item>
///   <item><description><see cref="BullTrap"/> — Initial bullish stretch (gap-up / morning rally) where intraday VIX fails to crush and price rolls over below VWAP.</description></item>
///   <item><description><see cref="Neutral"/> — No setup has cleared both the score threshold and its core confirmation checklist.</description></item>
/// </list>
/// </summary>
internal enum CascadeRegime
{
	Neutral,
	BearCascade,
	BearTrap,
	BullCascade,
	BullTrap,
}

/// <summary>
/// Multi-expiry + 0DTE gamma terrain read by <see cref="CascadeScreener"/>.
/// Wraps <see cref="SqueezeTerrain"/> for the front (0DTE) book alongside the multi-expiry (≤30 DTE)
/// gamma flip and net GEX fraction.
/// </summary>
internal sealed record CascadeTerrain(
	SqueezeTerrain Front,
	DateTime? FrontExpiry,
	decimal? Gravity0Dte,
	decimal? Centroid0Dte,
	decimal? NetPull0Dte,
	decimal? FlipMulti,
	decimal NetGexFractionMulti,
	decimal NetVexMulti,
	decimal NetVexFractionMulti,
	int ExpiryCount,
	decimal? OtmCallWall0Dte = null,
	decimal? OtmPutWall0Dte = null,
	decimal? CallWallMulti = null,
	decimal? PutWallMulti = null,
	decimal FrontNetGexFraction = 0m);

/// <summary>
/// Point-in-time flow, volatility, skew, and intraday tape inputs to <see cref="CascadeScreener.Evaluate"/>.
/// Any field may be null when its backing store is unavailable; missing required factors prevent confirmation.
/// </summary>
internal sealed record CascadeInputs(
	decimal? FlowShare,
	VolumePace? Volume,
	decimal? DeltaOiShare,
	DateTime? PriorOiDate,
	SessionTape? Tape,
	decimal? VixPriorClose,
	decimal? VixOpen,
	decimal? VixNow,
	decimal? MaxPriorVix9dOverVix,
	decimal? Hv3OverHv30,
	decimal? SmileIndex,
	decimal? SmileChange1d,
	decimal? SmileZScore20d = null)
{
	/// <summary>Intraday VIX percent change from the prior session's close (VIX_CC).</summary>
	public decimal? VixChangeFromClosePct => VixPriorClose is > 0m && VixNow is > 0m
		? (VixNow.Value - VixPriorClose.Value) / VixPriorClose.Value * 100m
		: null;

	/// <summary>Intraday VIX percent change from today's 09:30 RTH open (VIX_OTN).</summary>
	public decimal? VixChangeFromOpenPct => VixOpen is > 0m && VixNow is > 0m
		? (VixNow.Value - VixOpen.Value) / VixOpen.Value * 100m
		: null;

	/// <summary>Underlying percent change from today's 09:30 RTH session open (OTN).</summary>
	public decimal? SpotOpenToNowPct => Tape is { SessionOpen: > 0m }
		? (Tape.Spot - Tape.SessionOpen) / Tape.SessionOpen * 100m
		: null;
}

internal enum NoteSeverity
{
	Confirm,
	Warn,
	Conflict,
}

internal sealed record FactorScore(string Name, int? Points, int Max, string Detail);

internal sealed record SetupNote(NoteSeverity Severity, string Text);

/// <summary>Evaluation of one of the four candidate regimes (0–100 score + 5 factors + confirmation checklist).</summary>
internal sealed record CascadeSetupEvaluation(
	CascadeRegime Regime,
	int Score,
	bool Complete,
	IReadOnlyList<FactorScore> Factors,
	IReadOnlyList<SetupNote> Notes);

/// <summary>Complete output of <see cref="CascadeScreener.Evaluate"/>.</summary>
internal sealed record CascadeReading(
	CascadeRegime PrimaryRegime,
	CascadeSetupEvaluation LeadingSetup,
	IReadOnlyDictionary<CascadeRegime, CascadeSetupEvaluation> Setups,
	CascadeTerrain Terrain,
	CascadeInputs Inputs,
	int TriggerScore)
{
	/// <summary>True when the leading setup cleared both <see cref="TriggerScore"/> and its core confirmation gates.</summary>
	public bool Active => PrimaryRegime != CascadeRegime.Neutral;

	/// <summary>Direction the leading setup describes (confirmed or not): +1 for Bull Cascade / Bear Trap, −1 for Bear Cascade / Bull Trap.
	/// Descriptive only: over 2022–2026 a confirmed regime at 09:50 ET showed no edge in the move from there to the close.</summary>
	public int DirectionalSign => LeadingSetup.Regime is CascadeRegime.BullCascade or CascadeRegime.BearTrap ? 1 : -1;

	/// <summary>Short regime label for badges and logs.</summary>
	public static string RegimeLabel(CascadeRegime r) => r switch
	{
		CascadeRegime.BearCascade => "BEAR CASCADE",
		CascadeRegime.BearTrap => "BEAR TRAP",
		CascadeRegime.BullCascade => "BULL CASCADE",
		CascadeRegime.BullTrap => "BULL TRAP",
		_ => "NEUTRAL",
	};
}

/// <summary>
/// Pure-function classifier and 0–100 scorer for the four market movement regimes:
/// <see cref="CascadeRegime.BearCascade"/>, <see cref="CascadeRegime.BearTrap"/>,
/// <see cref="CascadeRegime.BullCascade"/>, and <see cref="CascadeRegime.BullTrap"/>.
/// Reuses <see cref="GexSqueezeScreener"/>'s terrain, flow, volume-pace, and ΔOI primitives alongside
/// multi-expiry GEX (<see cref="GexMatrix"/>), <see cref="SessionTape"/>, and intraday VIX velocity.
/// All price excursions are measured in 1-day expected moves (<see cref="SqueezeTerrain.DailyMove"/>) and
/// SMILE skew is normalized via a 20-day rolling z-score so thresholds remain stationary across eras.
/// </summary>
internal static class CascadeScreener
{
	public const int DefaultTriggerScore = 65;
	public const int StrongScore = 80;
	private const int FactorMax = 20;

	/// <summary>Extracts the 0DTE + multi-expiry GEX terrain from the built exposure matrix.</summary>
	public static CascadeTerrain TerrainFrom(GexMatrix gexMatrix, GexMatrix? vexMatrix, decimal spot, DateTime asOf)
	{
		// Build a 0DTE-scoped view for the front expiry so 0DTE flip/walls/gravity aren't diluted by 30DTE OI.
		var frontExp = gexMatrix.Expiries.Count > 0 ? gexMatrix.Expiries[0] : (DateTime?)null;
		var frontFlip = frontExp.HasValue ? gexMatrix.FindGammaFlip(spot, frontExp.Value) : null;
		var baseTerrain = GexSqueezeScreener.TerrainFrom(gexMatrix, spot, asOf);
		var minHeadroom = Math.Max(baseTerrain.DailyMove ?? 0m, spot * 0.005m) * 0.15m;
		decimal frontCallGex = 0m, frontPutGex = 0m;
		decimal? callWallAbove = null, putWallBelow = null;
		decimal bestCallAbove = 0m, bestPutBelow = 0m;
		decimal? otmCallWall0Dte = null, otmPutWall0Dte = null;
		decimal bestOtmCallNet = 0m, bestOtmPutNet = 0m;
		if (frontExp.HasValue)
		{
			foreach (var ((exp, strike), cell) in gexMatrix.FullCells)
			{
				if (exp != frontExp.Value) continue;
				frontCallGex += cell.CallGex;
				frontPutGex += cell.PutGex;
				if (strike >= spot && cell.CallGex > bestCallAbove) { bestCallAbove = cell.CallGex; callWallAbove = strike; }
				if (strike <= spot && cell.PutGex > bestPutBelow) { bestPutBelow = cell.PutGex; putWallBelow = strike; }
				if (strike >= spot + minHeadroom && cell.Net > bestOtmCallNet) { bestOtmCallNet = cell.Net; otmCallWall0Dte = strike; }
				if (strike <= spot - minHeadroom && -cell.Net > bestOtmPutNet) { bestOtmPutNet = -cell.Net; otmPutWall0Dte = strike; }
			}
		}
		var (frontCallWall, frontPutWall) = frontExp.HasValue ? gexMatrix.FindWalls(frontExp.Value) : (null, null);
		var frontShortGamma = frontFlip.HasValue ? spot < frontFlip.Value : (frontCallGex - frontPutGex) < 0m;
		var frontGross = frontCallGex + frontPutGex;
		var frontNetGexFraction = frontGross > 0m ? (frontCallGex - frontPutGex) / frontGross : 0m;

		var frontTerrain = new SqueezeTerrain(
			Spot: spot,
			Trigger: frontFlip ?? baseTerrain.Trigger,
			ShortGamma: frontShortGamma,
			CallWall: callWallAbove ?? frontCallWall ?? baseTerrain.CallWall,
			PutWall: putWallBelow ?? frontPutWall ?? baseTerrain.PutWall,
			DailyMove: baseTerrain.DailyMove);

		var gravity0Dte = frontExp.HasValue ? gexMatrix.GravityByExpiry.GetValueOrDefault(frontExp.Value) : null;
		var centroid0Dte = frontExp.HasValue ? gexMatrix.GrossCentroidByExpiry.GetValueOrDefault(frontExp.Value) : null;
		var netPull0Dte = frontExp.HasValue ? gexMatrix.NetPullByExpiry.GetValueOrDefault(frontExp.Value) : null;

		var flipMulti = gexMatrix.FindGammaFlip(spot);
		var totalGexGross = gexMatrix.TotalCallGex + gexMatrix.TotalPutGex;
		var netGexFractionMulti = totalGexGross > 0m ? (gexMatrix.TotalCallGex - gexMatrix.TotalPutGex) / totalGexGross : 0m;

		decimal netVexMulti = 0m, netVexFractionMulti = 0m;
		if (vexMatrix != null)
		{
			// Call vanna (>0 for OTM calls) and put vanna (<0 for OTM puts) carry opposite signs in GexMatrix:
			// dealer net VEX is CallVex − PutVex, whereas call-vs-put vanna skew is (|CallVex| − |PutVex|) / Gross.
			netVexMulti = vexMatrix.TotalCallGex - vexMatrix.TotalPutGex;
			var totalVexGross = Math.Abs(vexMatrix.TotalCallGex) + Math.Abs(vexMatrix.TotalPutGex);
			netVexFractionMulti = totalVexGross > 0m
				? Math.Clamp((Math.Abs(vexMatrix.TotalCallGex) - Math.Abs(vexMatrix.TotalPutGex)) / totalVexGross, -1m, 1m)
				: 0m;
		}

		return new CascadeTerrain(
			Front: frontTerrain,
			FrontExpiry: frontExp,
			Gravity0Dte: gravity0Dte,
			Centroid0Dte: centroid0Dte,
			NetPull0Dte: netPull0Dte,
			FlipMulti: flipMulti,
			NetGexFractionMulti: netGexFractionMulti,
			NetVexMulti: netVexMulti,
			NetVexFractionMulti: netVexFractionMulti,
			ExpiryCount: gexMatrix.Expiries.Count,
			OtmCallWall0Dte: otmCallWall0Dte,
			OtmPutWall0Dte: otmPutWall0Dte,
			CallWallMulti: baseTerrain.CallWall,
			PutWallMulti: baseTerrain.PutWall,
			FrontNetGexFraction: frontNetGexFraction);
	}

	/// <summary>Evaluates all four regimes and selects the active or leading classification.</summary>
	public static CascadeReading Evaluate(CascadeTerrain terrain, CascadeInputs inputs, int triggerScore = DefaultTriggerScore)
	{
		var bearCascade = ScoreCascade(terrain, inputs, CascadeRegime.BearCascade);
		var bearTrap = ScoreBearTrap(terrain, inputs);
		var bullCascade = ScoreCascade(terrain, inputs, CascadeRegime.BullCascade);
		var bullTrap = ScoreBullTrap(terrain, inputs);

		var setups = new Dictionary<CascadeRegime, CascadeSetupEvaluation>
		{
			[CascadeRegime.BearCascade] = bearCascade,
			[CascadeRegime.BearTrap] = bearTrap,
			[CascadeRegime.BullCascade] = bullCascade,
			[CascadeRegime.BullTrap] = bullTrap,
		};

		var confirmed = setups.Values
			.Where(s => s.Complete && s.Score >= triggerScore)
			.OrderByDescending(s => s.Score)
			.FirstOrDefault();

		var leading = confirmed ?? setups.Values.OrderByDescending(s => s.Score).First();
		var primary = confirmed?.Regime ?? CascadeRegime.Neutral;

		return new CascadeReading(primary, leading, setups, terrain, inputs, triggerScore);
	}

	// ── Shared cascade terrain (Bear Cascade and Bull Cascade) ─────────────────────────────────────────────

	/// <summary>The terrain both cascades require: dealers short gamma, so their hedging chases a move in either direction
	/// instead of damping it. True when the front expiry sits below its gamma flip or the multi-expiry book is net short.</summary>
	private static bool HasShortGammaTerrain(CascadeTerrain t) => t.Front.ShortGamma || t.NetGexFractionMulti <= -0.15m;

	/// <summary>Factor 1 of both cascades (<see cref="ScoreCascade"/>): up to 10 points for the front expiry's depth below its flip and up to 10 for
	/// the multi-expiry book's net short gamma. Direction-blind by design — the direction comes from the other factors.</summary>
	private static (int Points, string Detail) ShortGammaTerrainFactor(CascadeTerrain t, decimal move)
	{
		var f = t.Front;
		var pts0Dte = f.ShortGamma
			? (f.Trigger.HasValue ? 6m + 4m * Ramp(f.Trigger.Value - f.Spot, 0m, 0.5m * move) : 8m)
			: 0m;
		var ptsMulti = 10m * Ramp(-t.NetGexFractionMulti, 0.05m, 0.45m);
		if (t.FlipMulti.HasValue && f.Spot < t.FlipMulti.Value) ptsMulti = Math.Max(ptsMulti, 7m);
		return (RoundPts(pts0Dte + ptsMulti), $"{(f.ShortGamma ? "front short γ" : "front long γ")}, {t.ExpiryCount}exp net GEX {t.NetGexFractionMulti:+0%;-0%}");
	}

	private static SetupNote ShortGammaTerrainNote(CascadeTerrain t, string dampedMove) => HasShortGammaTerrain(t)
		? new SetupNote(NoteSeverity.Confirm, $"Short-gamma amplification active (front expiry {(t.Front.ShortGamma ? "below flip" : "above flip")}, {t.ExpiryCount}-exp net GEX {t.NetGexFractionMulti:+0%;-0%})")
		: new SetupNote(NoteSeverity.Conflict, $"Long-gamma cushion ({t.ExpiryCount}-exp net GEX {t.NetGexFractionMulti:+0%;-0%}) — dealers dampen {dampedMove}");

	// ── 1 & 3. BEAR CASCADE / BULL CASCADE (Continuation) ──────────────────────────────────────────────────

	/// <summary>Scores a continuation cascade. Bear Cascade and Bull Cascade are one rule set read in opposite
	/// directions: every directional input is signed by <c>dir</c> (+1 bull, −1 bear) so that positive means "with the
	/// cascade" — price away from the open, VIX moving the confirming way (down for bull, up for bear), flow and ΔOI on
	/// the cascade's side, gravity and the target wall ahead of spot. The terrain factor and the stress backdrop are
	/// direction-blind. No threshold, weight or gate exists for one direction only.</summary>
	private static CascadeSetupEvaluation ScoreCascade(CascadeTerrain t, CascadeInputs inp, CascadeRegime regime)
	{
		var bull = regime == CascadeRegime.BullCascade;
		var dir = bull ? 1m : -1m;
		var f = t.Front;
		var move = Math.Max(f.DailyMove ?? 0m, f.Spot * 0.006m);
		var withMoves = inp.Tape is { SessionOpen: > 0m } tp ? dir * (f.Spot - tp.SessionOpen) / move : (decimal?)null;
		var vixWithOpen = -dir * inp.VixChangeFromOpenPct;
		var vixWithClose = -dir * inp.VixChangeFromClosePct;
		var flowWith = dir * inp.FlowShare;
		var trendSide = bull ? VwapSide.Above : VwapSide.Below;

		// Factor 1: Short-Gamma Terrain (20 pts)
		var (f1Pts, f1Detail) = ShortGammaTerrainFactor(t, move);

		// Factor 2: Confirming Intraday VIX Move & Stress Backdrop (20 pts) — null when intraday VIX is absent
		int? f2Pts = null;
		if (vixWithOpen.HasValue || vixWithClose.HasValue)
		{
			decimal vixPts = 0m;
			if (vixWithOpen is { } vo)
				vixPts = Math.Max(vixPts, 14m * Ramp(vo, 0.5m, 4.5m));
			if (vixWithClose is { } vc && (vixWithOpen ?? 0m) >= 0m)
				vixPts = Math.Max(vixPts, 12m * Ramp(vc, 4.0m, 14.0m));

			decimal stressPts = 0m;
			if (inp.MaxPriorVix9dOverVix is { } bwd && bwd > 0m)
				stressPts += 3m * Ramp(bwd, 0.0m, 0.04m);
			if (inp.SmileZScore20d is { } sz && sz > 0.5m)
				stressPts += 3m * Ramp(sz, 0.5m, 2.0m);
			else if (inp.Hv3OverHv30 is { } whip && whip > 1.15m)
				stressPts += 2m * Ramp(whip, 1.15m, 1.60m);

			f2Pts = RoundPts(vixPts + Math.Min(6m, stressPts));
		}
		var f2Detail = FormatVolDetail(inp);

		// Factor 3: Flow on the Cascade's Side & Volume Pace (20 pts)
		decimal flowPts = flowWith is { } fw ? 14m * Ramp(fw, 0.10m, 0.45m) : 0m;
		decimal pacePts = 0m;
		if (inp.Volume is { } vp) pacePts += 4m * Ramp(vp.Ratio, 0.90m, 1.60m);
		if (dir * inp.DeltaOiShare is { } doiWith && doiWith > 0m) pacePts += 4m * Ramp(doiWith, 0.10m, 0.45m);
		var f3Pts = inp.FlowShare.HasValue ? RoundPts(flowPts + Math.Min(6m, pacePts)) : (int?)null;
		var f3Detail = FormatFlowDetail(inp, bull);

		// Factor 4: Gravity / Net Pull Ahead & Target Wall (20 pts)
		var targetWall = CascadeTargetWall(t, bull, move);
		var wallAhead = targetWall.HasValue ? dir * (targetWall.Value - f.Spot) : (decimal?)null;
		decimal gravPts = 0m;
		var anchor = t.Gravity0Dte ?? t.Centroid0Dte;
		if (anchor.HasValue && dir * (anchor.Value - f.Spot) >= 0m)
			gravPts = 10m * Ramp(dir * (anchor.Value - f.Spot), 0m, 0.50m * move);
		if (dir * t.NetPull0Dte is { } pullWith && pullWith > 0m)
			gravPts = Math.Max(gravPts, 10m * Ramp(pullWith, 0.10m, 0.65m));
		if (wallAhead >= 0.15m * move)
			gravPts += wallAhead.Value <= 1.5m * move ? 10m * Ramp(wallAhead.Value, 0.15m * move, 0.50m * move) : 5m;
		var f4Pts = RoundPts(gravPts);
		var f4Detail = $"front gravity {(t.Gravity0Dte.HasValue ? $"${t.Gravity0Dte.Value:N0}" : "—")}, pull {(t.NetPull0Dte.HasValue ? $"{t.NetPull0Dte.Value:+0.00;-0.00}σ" : "—")}, target {(bull ? "C" : "P")}-wall {(targetWall.HasValue ? $"${targetWall.Value:N0} ({targetWall.Value - f.Spot:+0;-0} pts)" : "—")}";

		// Factor 5: Session Tape & VWAP Hold (20 pts)
		int? f5Pts = null;
		var f5Detail = "no RTH session tape";
		if (inp.Tape is { } tape && withMoves.HasValue)
		{
			decimal tapePts = 0m;
			if (tape.Side == trendSide)
			{
				tapePts += 10m * Ramp(withMoves.Value, 0.15m, 0.60m);
				tapePts += 6m * Ramp(tape.MinutesHeldOnSide, 5m, 25m);
				tapePts += tape.VwapFlips <= 2 ? 4m : (tape.VwapFlips <= 4 ? 2m : 0m);
			}
			f5Pts = RoundPts(tapePts);
			f5Detail = $"spot {inp.SpotOpenToNowPct ?? 0m:+0.00;-0.00}% vs open ({dir * withMoves.Value:+0.00;-0.00} EM), {DescribeVwap(tape)} ({tape.MinutesHeldOnSide}m, {tape.VwapFlips} flips)";
		}

		var factors = new List<FactorScore>
		{
			new("Short-Gamma Terrain", f1Pts, FactorMax, f1Detail),
			new(bull ? "Intraday VIX Crush" : "Intraday VIX Expansion", f2Pts, FactorMax, f2Detail),
			new(bull ? "Call Flow & Pace" : "Put Flow & Pace", f3Pts, FactorMax, f3Detail),
			new("Gravity Pull & Target Wall", f4Pts, FactorMax, f4Detail),
			new(bull ? "VWAP Breakout Tape" : "VWAP Breakdown Tape", f5Pts, FactorMax, f5Detail),
		};

		var total = ScaleScore(factors);
		var vixConfirm = vixWithOpen.HasValue
			&& (vixWithOpen.Value >= 1.0m || ((vixWithClose ?? 0m) >= 6.0m && vixWithOpen.Value > 0m));
		// Gravity more than half an expected move BEHIND spot while VIX is not moving the cascade's way: the opposite trap, not a cascade.
		var gravityVeto = t.Gravity0Dte.HasValue && dir * (t.Gravity0Dte.Value - f.Spot) < -0.50m * move && (vixWithOpen ?? 0m) < 2.0m;
		var tapeHolds = inp.Tape is { MinutesHeldOnSide: >= 5 } held && held.Side == trendSide && (withMoves ?? 0m) >= 0.20m;
		var complete = factors.All(x => x.Points.HasValue)
			&& HasShortGammaTerrain(t)
			&& (flowWith ?? 0m) >= 0.18m
			&& tapeHolds
			&& vixConfirm
			&& !gravityVeto;

		var notes = BuildNotesCascade(t, inp, bull, vixConfirm, gravityVeto, tapeHolds, targetWall, withMoves);
		return new CascadeSetupEvaluation(regime, total, complete, factors, notes);
	}

	/// <summary>The wall a cascade is travelling toward — call side for bull, put side for bear: the front expiry's wall
	/// when it sits at least 0.15 expected moves ahead of spot, else the front expiry's largest out-of-the-money wall,
	/// else the multi-expiry wall if that is far enough ahead, else the (at-the-money) front wall.</summary>
	private static decimal? CascadeTargetWall(CascadeTerrain t, bool bull, decimal move)
	{
		var f = t.Front;
		var dir = bull ? 1m : -1m;
		var frontWall = bull ? f.CallWall : f.PutWall;
		if (frontWall.HasValue && dir * (frontWall.Value - f.Spot) >= 0.15m * move) return frontWall;
		var otmWall = bull ? t.OtmCallWall0Dte : t.OtmPutWall0Dte;
		if (otmWall.HasValue) return otmWall;
		var multiWall = bull ? t.CallWallMulti : t.PutWallMulti;
		return multiWall.HasValue && dir * (multiWall.Value - f.Spot) >= 0.15m * move ? multiWall : frontWall;
	}

	// ── 2. BEAR TRAP (Failed Breakdown / Squeeze Up) ───────────────────────────────────────────────────────

	private static CascadeSetupEvaluation ScoreBearTrap(CascadeTerrain t, CascadeInputs inp)
	{
		var f = t.Front;
		var move = Math.Max(f.DailyMove ?? 0m, f.Spot * 0.006m);
		var gapPct = inp.Tape?.GapPct ?? 0m;
		var gapMoves = inp.Tape != null && f.Spot > 0m ? (gapPct / 100m * f.Spot) / move : 0m;
		var dipMoves = inp.Tape != null ? Math.Max(0m, inp.Tape.SessionOpen - inp.Tape.SessionLow) / move : 0m;
		var stretchMoves = Math.Max(-gapMoves, dipMoves);
		var bounceMoves = inp.Tape != null ? Math.Max(0m, f.Spot - inp.Tape.SessionLow) / move : (decimal?)null;

		// Factor 1: Initial Bearish Stretch (20 pts)
		decimal stretchPts = 14m * Ramp(stretchMoves, 0.20m, 0.80m);
		if (stretchMoves >= 0.20m && (f.ShortGamma || t.NetGexFractionMulti < -0.15m))
			stretchPts += 6m;
		var f1Pts = RoundPts(stretchPts);
		var f1Detail = $"bear stretch {stretchMoves:F2} EM (gap {gapPct:+0.00;-0.00}%), {(f.ShortGamma ? "0DTE short γ" : "0DTE long γ")}, 30d GEX {t.NetGexFractionMulti:+0%;-0%}";

		// Factor 2: Intraday VIX Stall / Drop from Open (20 pts) — null when intraday VIX is absent
		int? f2Pts = null;
		if (inp.VixChangeFromOpenPct is { } vOtn)
		{
			var stallPts = stretchMoves >= 0.15m ? 20m * Ramp(0.8m - vOtn, 0m, 3.0m) : 0m;
			f2Pts = RoundPts(stallPts);
		}
		var f2Detail = $"VIX vs open {(inp.VixChangeFromOpenPct.HasValue ? $"{inp.VixChangeFromOpenPct.Value:+0.0;-0.0}%" : "n/a")}, vs close {(inp.VixChangeFromClosePct.HasValue ? $"{inp.VixChangeFromClosePct.Value:+0.0;-0.0}%" : "n/a")}";

		// Factor 3: Overhead 0DTE Gravity / Pull Magnet (20 pts)
		decimal overheadPts = 0m;
		var anchor = t.Gravity0Dte ?? t.Centroid0Dte;
		if (anchor.HasValue && anchor.Value > f.Spot)
			overheadPts = 20m * Ramp(anchor.Value - f.Spot, 0.05m * move, 0.60m * move);
		if (t.NetPull0Dte is { } pull && pull > 0m)
			overheadPts = Math.Max(overheadPts, 20m * Ramp(pull, 0.05m, 0.45m));
		var f3Pts = RoundPts(overheadPts);
		var f3Detail = $"0DTE gravity {(t.Gravity0Dte.HasValue ? $"${t.Gravity0Dte.Value:N0} ({t.Gravity0Dte.Value - f.Spot:+0;-0} pts)" : "—")}, pull {(t.NetPull0Dte.HasValue ? $"{t.NetPull0Dte.Value:+0.00;-0.00}σ" : "—")}";

		// Factor 4: Reversal / Trapped Flow (20 pts)
		decimal crowdPts = 0m;
		if (inp.FlowShare is { } fs && stretchMoves >= 0.15m)
		{
			if (fs > 0m)
				crowdPts = 16m * Ramp(fs, 0.05m, 0.35m); // call buyers stepping into the dip
			else if (fs < -0.15m && (inp.VixChangeFromOpenPct ?? 99m) <= 0.5m)
				crowdPts = 14m * Ramp(-fs, 0.15m, 0.45m); // trapped put buyers with stalling VIX
		}
		if (inp.Volume is { } vp && vp.Ratio >= 1.1m) crowdPts += 4m * Ramp(vp.Ratio, 1.1m, 1.8m);
		var f4Pts = inp.FlowShare.HasValue ? RoundPts(crowdPts) : (int?)null;
		var f4Detail = $"flow share {(inp.FlowShare.HasValue ? $"{inp.FlowShare.Value:+0.00;-0.00}" : "—")}, vol pace {(inp.Volume != null ? $"{inp.Volume.Ratio:F2}×" : "—")}";

		// Factor 5: VWAP Reclaim & Bounce off Session Low (20 pts)
		int? f5Pts = null;
		var f5Detail = "no RTH session tape";
		if (inp.Tape is { } tape && bounceMoves.HasValue)
		{
			decimal reclaimPts = 0m;
			if (stretchMoves >= 0.15m)
			{
				reclaimPts += 12m * Ramp(bounceMoves.Value, 0.20m, 0.65m);
				if (tape.Side == VwapSide.Above)
					reclaimPts += 8m;
			}
			f5Pts = RoundPts(reclaimPts);
			f5Detail = $"{DescribeVwap(tape)}, bounce +{f.Spot - tape.SessionLow:F1} pts ({bounceMoves.Value:F2} EM) off low (${tape.SessionLow:N2})";
		}

		var factors = new List<FactorScore>
		{
			new("Initial Bear Stretch", f1Pts, FactorMax, f1Detail),
			new("Intraday VIX Stall", f2Pts, FactorMax, f2Detail),
			new("Overhead Gravity Magnet", f3Pts, FactorMax, f3Detail),
			new("Trapped Put / Bid Flow", f4Pts, FactorMax, f4Detail),
			new("VWAP Reclaim & Bounce", f5Pts, FactorMax, f5Detail),
		};

		var allScored = factors.All(x => x.Points.HasValue);
		var total = ScaleScore(factors);
		var hadBearStretch = stretchMoves >= 0.25m;
		var vixNotExpanding = inp.VixChangeFromOpenPct is <= 0.5m;
		var reclaimedVwap = inp.Tape is { Side: VwapSide.Above } && (bounceMoves ?? 0m) >= 0.25m;
		var upwardStructure = (t.Gravity0Dte.HasValue && t.Gravity0Dte.Value > f.Spot)
			|| (t.NetPull0Dte ?? 0m) > 0.05m
			|| (inp.FlowShare ?? 0m) >= 0.10m;
		var complete = allScored && hadBearStretch && vixNotExpanding && reclaimedVwap && upwardStructure;

		var notes = BuildNotesBearTrap(t, inp, stretchMoves, hadBearStretch, vixNotExpanding, reclaimedVwap, upwardStructure);
		return new CascadeSetupEvaluation(CascadeRegime.BearTrap, total, complete, factors, notes);
	}

	// ── 4. BULL TRAP (Exhaustion Bounce / Failed Breakout — Fade Down) ─────────────────────────────────────

	private static CascadeSetupEvaluation ScoreBullTrap(CascadeTerrain t, CascadeInputs inp)
	{
		var f = t.Front;
		var move = Math.Max(f.DailyMove ?? 0m, f.Spot * 0.006m);
		var gapPct = inp.Tape?.GapPct ?? 0m;
		var gapMoves = inp.Tape != null && f.Spot > 0m ? (gapPct / 100m * f.Spot) / move : 0m;
		var rallyMoves = inp.Tape != null ? Math.Max(0m, inp.Tape.SessionHigh - inp.Tape.SessionOpen) / move : 0m;
		var stretchMoves = Math.Max(gapMoves, rallyMoves);
		var fadeMoves = inp.Tape != null ? Math.Max(0m, inp.Tape.SessionHigh - f.Spot) / move : (decimal?)null;
		var rejectedCallWall = f.CallWall.HasValue && inp.Tape != null
			&& inp.Tape.SessionHigh >= f.CallWall.Value - 0.15m * move
			&& f.Spot <= f.CallWall.Value
			&& (fadeMoves ?? 0m) >= 0.20m;

		// Factor 1: Initial Bullish Stretch into Gamma Ceiling (20 pts)
		decimal ceilingPts = 14m * Ramp(stretchMoves, 0.20m, 0.80m);
		if (stretchMoves >= 0.20m && (rejectedCallWall || t.NetGexFractionMulti < -0.15m))
			ceilingPts += 6m;
		var f1Pts = RoundPts(ceilingPts);
		var f1Detail = $"bull stretch {stretchMoves:F2} EM (gap {gapPct:+0.00;-0.00}%), 0DTE call wall {(f.CallWall.HasValue ? $"${f.CallWall.Value:N0}" : "—")}";

		// Factor 2: Intraday VIX Non-Confirmation & Skew Stress (20 pts) — null when intraday VIX is absent
		int? f2Pts = null;
		if (inp.VixChangeFromOpenPct is { } vOtn)
		{
			decimal vixDivergePts = stretchMoves >= 0.15m ? 14m * Ramp(vOtn + 0.5m, 0m, 2.5m) : 0m;
			decimal skewWarnPts = 0m;
			if (inp.SmileZScore20d is { } sz && sz >= 0.75m)
				skewWarnPts += 3m * Ramp(sz, 0.75m, 2.25m);
			if (inp.MaxPriorVix9dOverVix is { } bwd && bwd > 0.01m)
				skewWarnPts += 3m * Ramp(bwd, 0.01m, 0.05m);
			f2Pts = RoundPts(vixDivergePts + skewWarnPts);
		}
		var f2Detail = $"VIX vs open {(inp.VixChangeFromOpenPct.HasValue ? $"{inp.VixChangeFromOpenPct.Value:+0.0;-0.0}%" : "n/a")}, SMILE z20 {(inp.SmileZScore20d.HasValue ? $"{inp.SmileZScore20d.Value:+0.0;-0.0}σ" : "n/a")}";

		// Factor 3: Downward Gravity / Wall Rejection Drag (20 pts)
		decimal dragPts = 0m;
		var anchor = t.Gravity0Dte ?? t.Centroid0Dte;
		if (anchor.HasValue && anchor.Value < f.Spot)
			dragPts = 20m * Ramp(f.Spot - anchor.Value, 0.05m * move, 0.60m * move);
		if (t.NetPull0Dte is { } pull && pull < 0m)
			dragPts = Math.Max(dragPts, 20m * Ramp(-pull, 0.05m, 0.45m));
		if (rejectedCallWall && fadeMoves.HasValue)
			dragPts = Math.Max(dragPts, 14m + 6m * Ramp(fadeMoves.Value, 0.20m, 0.50m));
		var f3Pts = RoundPts(dragPts);
		var f3Detail = rejectedCallWall
			? $"rejected 0DTE call wall ${f.CallWall:N0} (high ${inp.Tape!.SessionHigh:N2} → ${f.Spot:N2})"
			: $"0DTE gravity {(t.Gravity0Dte.HasValue ? $"${t.Gravity0Dte.Value:N0} ({t.Gravity0Dte.Value - f.Spot:+0;-0} pts)" : "—")}, pull {(t.NetPull0Dte.HasValue ? $"{t.NetPull0Dte.Value:+0.00;-0.00}σ" : "—")}";

		// Factor 4: Reversal / Exhaustion Flow (20 pts)
		decimal exhaustPts = 0m;
		if (inp.FlowShare is { } fs && stretchMoves >= 0.15m)
		{
			if (fs < 0m)
				exhaustPts = 16m * Ramp(-fs, 0.05m, 0.35m); // put buyers stepping into the rally
			else if (fs > 0.15m && (inp.VixChangeFromOpenPct ?? -99m) >= -0.5m)
				exhaustPts = 14m * Ramp(fs, 0.15m, 0.45m); // trapped call buyers with non-confirming VIX
		}
		if (inp.Volume is { } vp && vp.Ratio >= 1.1m) exhaustPts += 4m * Ramp(vp.Ratio, 1.1m, 1.8m);
		var f4Pts = inp.FlowShare.HasValue ? RoundPts(exhaustPts) : (int?)null;
		var f4Detail = $"flow share {(inp.FlowShare.HasValue ? $"{inp.FlowShare.Value:+0.00;-0.00}" : "—")}, vol pace {(inp.Volume != null ? $"{inp.Volume.Ratio:F2}×" : "—")}";

		// Factor 5: VWAP Loss & Roll-Over off Session High (20 pts)
		int? f5Pts = null;
		var f5Detail = "no RTH session tape";
		if (inp.Tape is { } tape && fadeMoves.HasValue)
		{
			decimal rollPts = 0m;
			if (stretchMoves >= 0.15m)
			{
				rollPts += 12m * Ramp(fadeMoves.Value, 0.20m, 0.65m);
				if (tape.Side == VwapSide.Below)
					rollPts += 8m;
			}
			f5Pts = RoundPts(rollPts);
			f5Detail = $"{DescribeVwap(tape)}, fade −{tape.SessionHigh - f.Spot:F1} pts ({fadeMoves.Value:F2} EM) off high (${tape.SessionHigh:N2})";
		}

		var factors = new List<FactorScore>
		{
			new("Initial Bull Stretch", f1Pts, FactorMax, f1Detail),
			new("VIX Non-Confirmation", f2Pts, FactorMax, f2Detail),
			new("Wall Rejection / Gravity", f3Pts, FactorMax, f3Detail),
			new("Put / Exhaustion Flow", f4Pts, FactorMax, f4Detail),
			new("VWAP Loss & Roll-Over", f5Pts, FactorMax, f5Detail),
		};

		var allScored = factors.All(x => x.Points.HasValue);
		var total = ScaleScore(factors);
		var hadBullStretch = stretchMoves >= 0.25m;
		var vixNotCrushing = inp.VixChangeFromOpenPct is >= -0.5m;
		var lostVwap = inp.Tape is { Side: VwapSide.Below } && (fadeMoves ?? 0m) >= 0.25m;
		var downwardStructure = (t.Gravity0Dte.HasValue && t.Gravity0Dte.Value < f.Spot)
			|| (t.NetPull0Dte ?? 0m) < -0.05m
			|| (inp.FlowShare ?? 0m) <= -0.10m
			|| rejectedCallWall;
		var complete = allScored && hadBullStretch && vixNotCrushing && lostVwap && downwardStructure;

		var notes = BuildNotesBullTrap(t, inp, stretchMoves, hadBullStretch, vixNotCrushing, lostVwap, downwardStructure, rejectedCallWall);
		return new CascadeSetupEvaluation(CascadeRegime.BullTrap, total, complete, factors, notes);
	}

	// ── Checklist Notes Builders ───────────────────────────────────────────────────────────────────────────

	private static List<SetupNote> BuildNotesCascade(CascadeTerrain t, CascadeInputs inp, bool bull, bool vixConfirm, bool gravityVeto, bool tapeHolds, decimal? targetWall, decimal? withMoves)
	{
		var notes = new List<SetupNote>();
		var f = t.Front;
		var dir = bull ? 1m : -1m;
		var move = Math.Max(f.DailyMove ?? 0m, f.Spot * 0.006m);
		var side = bull ? "call" : "put";
		var oppositeTrap = bull ? "Bull Trap" : "Bear Trap";
		notes.Add(ShortGammaTerrainNote(t, bull ? "rallies" : "selloffs"));

		notes.Add(vixConfirm
			? new SetupNote(NoteSeverity.Confirm, $"Intraday VIX {(bull ? "crushing" : "expanding")} ({FormatVixMoves(inp)}) — confirms the move against a {oppositeTrap}")
			: new SetupNote(NoteSeverity.Conflict, $"Intraday VIX is not {(bull ? "crushing" : "expanding")} ({FormatVixMoves(inp)}) — watch for a {oppositeTrap}"));

		if (inp.FlowShare is { } fs)
		{
			notes.Add(dir * fs >= 0.18m
				? new SetupNote(NoteSeverity.Confirm, $"Strong Δ-weighted {side} flow ({fs:+0.00;-0.00}, {(1m + dir * fs) / 2m:P0} {side} share)")
				: new SetupNote(NoteSeverity.Conflict, $"{(bull ? "Call" : "Put")} flow insufficient ({fs:+0.00;-0.00}) for a cascade"));
		}

		if (gravityVeto)
			notes.Add(new SetupNote(NoteSeverity.Conflict, $"Front gravity (${t.Gravity0Dte:N0}) sits well {(bull ? "below" : "above")} spot (${f.Spot:N2}) while VIX is tame — {oppositeTrap} risk"));
		else if (t.Gravity0Dte.HasValue)
			notes.Add(new SetupNote(dir * (t.Gravity0Dte.Value - f.Spot) >= 0m ? NoteSeverity.Confirm : NoteSeverity.Warn, $"Front gravity at ${t.Gravity0Dte.Value:N0} ({t.Gravity0Dte.Value - f.Spot:+0.0;-0.0} pts vs spot, pull {t.NetPull0Dte ?? 0m:+0.00;-0.00}σ)"));

		if (targetWall.HasValue)
		{
			var ahead = dir * (targetWall.Value - f.Spot);
			notes.Add(ahead >= 0.15m * move
				? new SetupNote(NoteSeverity.Confirm, $"{(bull ? "Overhead call" : "Downside put")} wall target at ${targetWall.Value:N0} ({targetWall.Value - f.Spot:+0;-0} pts of room)")
				: new SetupNote(NoteSeverity.Warn, $"Spot is at the front {side} wall ${targetWall.Value:N0} ({targetWall.Value - f.Spot:+0;-0} pts) with no farther wall ahead — watch for a pin/stall"));
		}

		if (inp.Tape is { } tape)
		{
			notes.Add(tapeHolds
				? new SetupNote(NoteSeverity.Confirm, $"Holding {(bull ? "above" : "below")} session VWAP ${tape.Vwap:N2} for {tape.MinutesHeldOnSide}m ({inp.SpotOpenToNowPct:+0.00;-0.00}% / {dir * (withMoves ?? 0m):+0.00;-0.00} EM from open)")
				: new SetupNote(NoteSeverity.Conflict, $"Price is not holding ≥0.20 EM {(bull ? "above" : "below")} the open on the {(bull ? "upper" : "lower")} side of VWAP for 5m (${tape.Vwap:N2}, {DescribeVwap(tape)}, {dir * (withMoves ?? 0m):+0.00;-0.00} EM)"));
		}
		return notes;
	}

	private static List<SetupNote> BuildNotesBearTrap(CascadeTerrain t, CascadeInputs inp, decimal stretchMoves, bool hadBearStretch, bool vixNotExpanding, bool reclaimedVwap, bool upwardStructure)
	{
		var notes = new List<SetupNote>();
		var f = t.Front;
		notes.Add(hadBearStretch
			? new SetupNote(NoteSeverity.Confirm, $"Initial bearish stretch present ({stretchMoves:F2} EM, gap {inp.Tape?.GapPct ?? 0m:+0.00;-0.00}%)")
			: new SetupNote(NoteSeverity.Warn, $"Insufficient morning bear stretch ({stretchMoves:F2} EM; need ≥0.25 EM)"));

		notes.Add(vixNotExpanding
			? new SetupNote(NoteSeverity.Confirm, $"VIX stalling/dropping from open ({FormatVixMoves(inp)}) — sellers lack vol follow-through")
			: new SetupNote(NoteSeverity.Conflict, $"VIX is expanding intraday ({FormatVixMoves(inp)}) — points to Bear Cascade, not a trap"));

		notes.Add(upwardStructure
			? new SetupNote(NoteSeverity.Confirm, $"Upward structure support (0DTE gravity {(t.Gravity0Dte.HasValue ? $"${t.Gravity0Dte.Value:N0}" : "—")}, pull {t.NetPull0Dte ?? 0m:+0.00;-0.00}σ, flow {inp.FlowShare ?? 0m:+0.00;-0.00})")
			: new SetupNote(NoteSeverity.Warn, $"0DTE gravity/pull/flow still leaning below spot"));

		if (inp.Tape is { } tape)
		{
			notes.Add(reclaimedVwap
				? new SetupNote(NoteSeverity.Confirm, $"Reclaimed session VWAP (${tape.Vwap:N2}), bouncing +{f.Spot - tape.SessionLow:F1} pts off low (${tape.SessionLow:N2})")
				: new SetupNote(NoteSeverity.Conflict, $"Waiting for VWAP reclaim & ≥0.25 EM bounce (${tape.Vwap:N2}, {DescribeVwap(tape)})"));
		}
		return notes;
	}

	private static List<SetupNote> BuildNotesBullTrap(CascadeTerrain t, CascadeInputs inp, decimal stretchMoves, bool hadBullStretch, bool vixNotCrushing, bool lostVwap, bool downwardStructure, bool rejectedCallWall)
	{
		var notes = new List<SetupNote>();
		var f = t.Front;
		notes.Add(hadBullStretch
			? new SetupNote(NoteSeverity.Confirm, $"Initial bullish stretch present ({stretchMoves:F2} EM, high ${inp.Tape?.SessionHigh ?? f.Spot:N2})")
			: new SetupNote(NoteSeverity.Warn, $"Insufficient morning bull stretch ({stretchMoves:F2} EM; need ≥0.25 EM)"));

		notes.Add(vixNotCrushing
			? new SetupNote(NoteSeverity.Confirm, $"VIX refusing to crush from open ({FormatVixMoves(inp)}) — rally lacks vol confirmation")
			: new SetupNote(NoteSeverity.Conflict, $"VIX is crushing ({FormatVixMoves(inp)}) — favors genuine Bull Cascade"));

		if (rejectedCallWall && inp.Tape != null)
			notes.Add(new SetupNote(NoteSeverity.Confirm, $"Rejected 0DTE Call Wall at ${f.CallWall:N0} (tagged ${inp.Tape.SessionHigh:N2}, now ${f.Spot:N2})"));
		else
			notes.Add(downwardStructure
				? new SetupNote(NoteSeverity.Confirm, $"Downward drag active (0DTE gravity {(t.Gravity0Dte.HasValue ? $"${t.Gravity0Dte.Value:N0}" : "—")}, pull {t.NetPull0Dte ?? 0m:+0.00;-0.00}σ, flow {inp.FlowShare ?? 0m:+0.00;-0.00})")
				: new SetupNote(NoteSeverity.Warn, "0DTE gravity/pull/flow still leaning above spot"));

		if (inp.Tape is { } tape)
		{
			notes.Add(lostVwap
				? new SetupNote(NoteSeverity.Confirm, $"Lost session VWAP (${tape.Vwap:N2}), fading −{tape.SessionHigh - f.Spot:F1} pts off high (${tape.SessionHigh:N2})")
				: new SetupNote(NoteSeverity.Conflict, $"Waiting for VWAP loss & ≥0.25 EM fade (${tape.Vwap:N2}, {DescribeVwap(tape)})"));
		}
		return notes;
	}

	// ── Formatting & Math Helpers ──────────────────────────────────────────────────────────────────────────

	private static string FormatVolDetail(CascadeInputs inp)
	{
		var parts = new List<string>();
		if (inp.VixNow is { } vn) parts.Add($"VIX {vn:F2}");
		if (inp.VixChangeFromOpenPct is { } vo) parts.Add($"{vo:+0.0;-0.0}% vs open");
		if (inp.VixChangeFromClosePct is { } vc) parts.Add($"{vc:+0.0;-0.0}% vs close");
		if (inp.MaxPriorVix9dOverVix is { } bwd) parts.Add($"3d VIX9D/VIX {bwd * 100m:+0.0;-0.0}%");
		if (inp.SmileIndex is { } sm)
		{
			var zTag = inp.SmileZScore20d is { } sz ? $", {sz:+0.0;-0.0}σ" : "";
			parts.Add($"SMILE {sm:F0}{(inp.SmileChange1d is { } d ? $" ({d:+0;-0}{zTag})" : (zTag.Length > 0 ? $" ({zTag.TrimStart(',', ' ')})" : ""))}");
		}
		return parts.Count > 0 ? string.Join(", ", parts) : "no VIX/SMILE data";
	}

	private static string FormatVixMoves(CascadeInputs inp)
	{
		var parts = new List<string>();
		if (inp.VixChangeFromOpenPct is { } vo) parts.Add($"{vo:+0.0;-0.0}% vs open");
		if (inp.VixChangeFromClosePct is { } vc) parts.Add($"{vc:+0.0;-0.0}% vs close");
		return parts.Count > 0 ? string.Join(", ", parts) : "no intraday VIX";
	}

	private static string FormatFlowDetail(CascadeInputs inp, bool isBull)
	{
		if (inp.FlowShare is not { } fs) return "no option volume";
		var sidePct = isBull ? (1m + fs) / 2m : (1m - fs) / 2m;
		var pace = inp.Volume is { } vp ? $", pace {vp.Ratio:F2}×" : "";
		var doi = inp.DeltaOiShare is { } d ? $", ΔOI {d:+0.00;-0.00}" : "";
		return $"{sidePct:P0} {(isBull ? "call" : "put")} Δ-vol ({fs:+0.00;-0.00}){pace}{doi}";
	}

	private static string DescribeVwap(SessionTape tape) => tape.Side switch
	{
		VwapSide.Above => "above VWAP",
		VwapSide.Below => "below VWAP",
		_ => "on VWAP",
	};

	private static decimal Ramp(decimal x, decimal lo, decimal hi)
		=> hi <= lo ? 0m : Math.Clamp((x - lo) / (hi - lo), 0m, 1m);

	private static int RoundPts(decimal pts)
		=> (int)Math.Round(Math.Clamp(pts, 0m, FactorMax), MidpointRounding.AwayFromZero);

	/// <summary>Scales earned points to 0–100 over the factors that could be scored. A reading with an unscored
	/// factor can still show a high number, but it never confirms: every setup's checklist requires all five.</summary>
	private static int ScaleScore(IReadOnlyList<FactorScore> factors)
	{
		var earned = 0;
		var possible = 0;
		foreach (var f in factors)
		{
			if (!f.Points.HasValue) continue;
			earned += f.Points.Value;
			possible += f.Max;
		}
		if (possible <= 0) return 0;
		var raw = (int)Math.Round(100m * earned / possible, MidpointRounding.AwayFromZero);
		return raw;
	}

	// ── Causal Multi-Expiry Chain Helpers (Zero EOD Look-Ahead & Backtest/Live Parity) ─────────────────────

	private static readonly ConcurrentDictionary<string, (DateTime Date, DateTime? PriorDate, Dictionary<string, OptionContractQuote> Quotes)> PriorMultiExpiryCache = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Loads and memoizes the prior trading day's (<c>priorDate &lt; date</c>) option chain from <c>data/oi/&lt;ticker&gt;/&lt;priorDate&gt;.jsonl</c>
	/// with each contract's IV solved as of <c>priorDate</c>'s close and all <c>Bid</c>/<c>Ask</c>/<c>LastPrice</c>/<c>Volume</c> cleared
	/// so <see cref="GexMatrix.Build"/> uses only causal prior-close IVs and never re-solves from a same-day 16:00 EOD mid.
	/// </summary>
	internal static (DateTime? PriorDate, Dictionary<string, OptionContractQuote> Quotes) GetCausalPriorDayChain(string ticker, DateTime date)
	{
		var rootKey = ticker.ToUpperInvariant();
		if (PriorMultiExpiryCache.TryGetValue(rootKey, out var cached) && cached.Date == date.Date)
			return (cached.PriorDate, cached.Quotes);

		var result = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		DateTime? foundDate = null;
		for (var d = date.Date.AddDays(-1); d >= date.Date.AddDays(-7); d = d.AddDays(-1))
		{
			var path = Program.ResolvePath($"data/oi/{rootKey}/{d:yyyy-MM-dd}.jsonl");
			if (!File.Exists(path)) continue;
			var snapshot = AnalyzeGexCommand.LoadOiSnapshot(path);
			if (snapshot.Quotes.Count == 0 || snapshot.Spot is not > 0m) continue;
			foundDate = d.Date;
			var priorSpot = snapshot.Spot.Value;
			foreach (var (sym, q) in snapshot.Quotes)
			{
				if (q.OpenInterest is not > 0) continue;
				var p = ParsingHelpers.ParseOptionSymbol(sym);
				if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, rootKey, p.ExpiryDate)) continue;
				if (p.ExpiryDate.Date < date.Date) continue;

				var iv = q.ImpliedVolatility is > 0.011m and < 4.99m ? q.ImpliedVolatility.Value : 0m;
				if (iv <= 0m && !string.IsNullOrEmpty(p.CallPut))
				{
					var mid = q.Bid is > 0m && q.Ask is > 0m ? (q.Bid.Value + q.Ask.Value) / 2m : q.LastPrice ?? 0m;
					if (mid > 0m)
					{
						var tPrior = MarketClock.CloseToClose(p.Root, d.Date, p.ExpiryDate, minDays: 1);
						var solved = OptionMath.ImpliedVol(priorSpot, p.Strike, tPrior, OptionMath.RiskFreeRate, mid, p.CallPut);
						if (solved > 0.011m && solved < 4.99m) iv = solved;
					}
				}
				if (iv <= 0m) continue;

				result[sym] = new OptionContractQuote(
					ContractSymbol: sym,
					LastPrice: null,
					Bid: null,
					Ask: null,
					Change: null,
					PercentChange: null,
					Volume: null,
					OpenInterest: q.OpenInterest,
					ImpliedVolatility: iv,
					HistoricalVolatility: iv);
			}
			break;
		}

		PriorMultiExpiryCache[rootKey] = (date.Date, foundDate, result);
		return (foundDate, result);
	}

	/// <summary>
	/// Builds a strictly causal historical chain for <c>wa analyze cascade --date</c>:
	/// strips all 16:00 EOD prices/IVs/volumes from <paramref name="rawDayQuotes"/>, prices non-front expiries from
	/// the prior trading day's snapshot (<c>priorDate &lt; asOfEt.Date</c>), and prices the front expiry from
	/// <c>quotes.db</c> minute NBBO at <paramref name="asOfEt"/> (or prior-day ex-ante IVs when <paramref name="forceExante"/>
	/// is set or <c>quotes.db</c> has no coverage).
	/// </summary>
	internal static Dictionary<string, OptionContractQuote> BuildCausalHistoricalChain(
		string ticker,
		DateTime asOfEt,
		Dictionary<string, OptionContractQuote> rawDayQuotes,
		bool forceExante,
		out string provenanceNote)
	{
		var date = asOfEt.Date;
		var (priorDate, priorChain) = GetCausalPriorDayChain(ticker, date);
		var frontExpiry = rawDayQuotes.Keys
			.Select(ParsingHelpers.ParseOptionSymbol)
			.Where(p => p != null && ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate) && p.ExpiryDate.Date >= date)
			.Select(p => p!.ExpiryDate.Date)
			.OrderBy(d => d)
			.FirstOrDefault();

		var causal = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

		// 1. Non-front expiries (1..30 DTE): take today's pre-open OCC OI paired strictly with prior-day settled IV
		foreach (var (sym, rawQ) in rawDayQuotes)
		{
			if (rawQ.OpenInterest is not > 0) continue;
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p == null || !ParsingHelpers.RootsMatchForAggregation(p.Root, ticker, p.ExpiryDate) || p.ExpiryDate.Date < date) continue;
			if (frontExpiry != default && p.ExpiryDate.Date == frontExpiry) continue;

			if (priorChain.TryGetValue(sym, out var pq) && pq.ImpliedVolatility is > 0m)
			{
				causal[sym] = new OptionContractQuote(
					ContractSymbol: sym,
					LastPrice: null,
					Bid: null,
					Ask: null,
					Change: null,
					PercentChange: null,
					Volume: null,
					OpenInterest: rawQ.OpenInterest,
					ImpliedVolatility: pq.ImpliedVolatility,
					HistoricalVolatility: pq.ImpliedVolatility);
			}
		}

		// 2. Front expiry (0DTE / nearest): price from quotes.db minute NBBO at asOfEt, else prior-day ex-ante IV
		int frontTimedCount = 0, frontExanteCount = 0;
		if (frontExpiry != default)
		{
			if (!forceExante)
			{
				var slice = AnalyzeGexCommand.IntradayQuoteSlice.Open(ticker, date, frontExpiry, rawDayQuotes);
				var timedQuotes = slice?.At(asOfEt);
				if (timedQuotes is { Count: > 0 })
				{
					foreach (var (sym, tq) in timedQuotes)
					{
						// Keep pre-open OI and minute-NBBO bid/ask; strip snapshot EOD volume (BuildInputsAsync reads time-gated store ohlcv)
						causal[sym] = tq with { Volume = null, ImpliedVolatility = null, HistoricalVolatility = null };
						frontTimedCount++;
					}
				}
			}

			if (frontTimedCount == 0)
			{
				foreach (var (sym, rawQ) in rawDayQuotes)
				{
					if (rawQ.OpenInterest is not > 0) continue;
					var p = ParsingHelpers.ParseOptionSymbol(sym);
					if (p == null || p.ExpiryDate.Date != frontExpiry) continue;
					if (priorChain.TryGetValue(sym, out var pq) && pq.ImpliedVolatility is > 0m)
					{
						causal[sym] = new OptionContractQuote(
							ContractSymbol: sym,
							LastPrice: null,
							Bid: null,
							Ask: null,
							Change: null,
							PercentChange: null,
							Volume: null,
							OpenInterest: rawQ.OpenInterest,
							ImpliedVolatility: pq.ImpliedVolatility,
							HistoricalVolatility: pq.ImpliedVolatility);
						frontExanteCount++;
					}
				}
			}
		}

		var backDesc = priorDate.HasValue
			? $"back-expiries priced from prior-day ({priorDate:yyyy-MM-dd}) settled IVs"
			: "no prior-day snapshot for back-expiries";
		provenanceNote = frontTimedCount > 0
			? $"Front-expiry ({frontExpiry:yyyy-MM-dd}) IVs back-solved from minute NBBO at {asOfEt:HH:mm} ET (data/quotes.db, {frontTimedCount} contracts); {backDesc}."
			: $"Front-expiry ({frontExpiry:yyyy-MM-dd}) priced from prior-day ex-ante IVs ({frontExanteCount} contracts); {backDesc}.";

		return causal;
	}

	// ── Shared Input Builder (CLI + Opener / WatchLoop / BacktestRunner) ───────────────────────────────────

	/// <summary>
	/// Builds <see cref="CascadeInputs"/> as of <paramref name="asOfEt"/> (Eastern wall-clock time) using the
	/// same data stores across <c>wa analyze cascade</c>, <c>wa ai watch</c>, and <c>wa ai backtest</c>.
	/// Never writes to <c>quotes.db</c>.
	/// </summary>
	public static async Task<CascadeInputs> BuildInputsAsync(
		string ticker,
		decimal spot,
		DateTime asOfEt,
		GexMatrix gexMatrix,
		HistoricalPriceCache priceCache,
		IntradayBarCache? intradayCache = null,
		SmileIndexCache? smileCache = null,
		IReadOnlyList<MinuteBar>? preloadedSessionRthBars = null,
		decimal? preloadedPrevClose = null,
		string? vendorName = null,
		decimal strikeRangeFraction = 0.20m,
		bool includeCurrentBar = true,
		CancellationToken cancellation = default)
	{
		vendorName ??= LiveQuoteSource.VendorName(LiveQuoteSource.ResolveVendor(null));
		var date = asOfEt.Date;
		var tod = asOfEt.TimeOfDay;
		var frontExp = gexMatrix.Expiries.Count > 0 ? gexMatrix.Expiries[0] : (DateTime?)null;

		// 1. FlowShare & VolumePace (front expiry, strictly time-gated up to asOfEt)
		decimal? flowShare = null;
		VolumePace? volumePace = null;
		if (frontExp.HasValue)
		{
			var frontContribs = gexMatrix.Contributors.Where(c => c.Expiry == frontExp.Value).ToList();
			var storeTape = AnalyzeGexCommand.LoadStoreTape(ticker, date, frontExp.Value);
			var hasLiveContribVol = frontContribs.Any(c => c.Volume > 0);
			if (storeTape.Count > 0 && (date < MarketTime.NowEt().Date || !hasLiveContribVol))
			{
				var cumByStrike = new Dictionary<decimal, (long C, long P)>();
				foreach (var (ts, byStrike) in storeTape)
				{
					if (ts > tod) break;
					foreach (var (k, v) in byStrike)
					{
						cumByStrike.TryGetValue(k, out var cur);
						cumByStrike[k] = (cur.C + (long)v.C, cur.P + (long)v.P);
					}
				}
				var timedContribs = frontContribs
					.Select(c => cumByStrike.TryGetValue(c.Strike, out var vp)
						? c with { Volume = c.IsCall ? vp.C : vp.P }
						: c with { Volume = 0L })
					.ToList();
				flowShare = GexSqueezeScreener.FlowShare(timedContribs, spot);
			}
			else if (hasLiveContribVol && date == MarketTime.NowEt().Date)
			{
				// Today's live chain: cumulative volume as of the fetch. A past date without store ohlcv stays null
				// (its snapshot volume is the 16:00 total), which leaves the flow factor unscored.
				flowShare = GexSqueezeScreener.FlowShare(frontContribs, spot);
			}

			var rawCaptures = IvDumpStore.LoadVolumeSeries(ticker, date, vendorName, (exp, strike) => exp == frontExp.Value && Math.Abs(strike - spot) / spot <= strikeRangeFraction);
			var captures = new SortedDictionary<TimeSpan, Dictionary<(DateTime Expiry, decimal Strike, string Right), long>>();
			foreach (var (ts, map) in rawCaptures)
				if (ts <= tod) captures[ts] = map;
			var window = GexSqueezeScreener.RecentVolumeWindow(captures, AnalyzeGexSettings.RthOpen);
			if (window == null && storeTape.Count > 0 && tod >= new TimeSpan(9, 35, 0))
			{
				var winStart = tod - TimeSpan.FromMinutes(15);
				if (winStart < AnalyzeGexSettings.RthOpen) winStart = AnalyzeGexSettings.RthOpen;
				long winVol = 0, sessionVol = 0;
				foreach (var (ts, byStrike) in storeTape)
				{
					if (ts > tod) break;
					long barVol = 0;
					foreach (var (_, v) in byStrike) barVol += (long)(v.C + v.P);
					sessionVol += barVol;
					if (ts >= winStart) winVol += barVol;
				}
				if (winVol > 0 && tod > winStart)
					window = new VolumeWindow(winStart, tod, winVol, sessionVol);
			}
			if (window != null)
			{
				var history = GexSqueezeScreener.SameWindowHistory(
					date,
					window,
					d => AnalyzeGexCommand.StoreMinuteVolume(ticker, d, d.AddDays((frontExp.Value - date).Days)));
				volumePace = GexSqueezeScreener.VolumePaceFrom(window, AnalyzeGexSettings.RthOpen, history);
			}
		}

		// 2. Prior-day ΔOI share
		var (priorOiDate, priorOi) = AnalyzeGexCommand.LoadPriorOi(ticker, date);
		var deltaOiShare = priorOi != null ? GexSqueezeScreener.DeltaOiShare(gexMatrix.Contributors, priorOi) : null;

		// 3. Prior daily closes (ticker, VIX, VIX9D) for PrevClose, VixPriorClose, VIX9D/VIX backwardation, and HV3/HV30
		decimal? prevClose = preloadedPrevClose;
		decimal? hv3OverHv30 = null;
		try
		{
			var closes = await priceCache.GetRecentClosesAsync(ticker, 32, date, cancellation);
			if (closes.Count == 0 && string.Equals(ticker, "SPXW", StringComparison.OrdinalIgnoreCase))
				closes = await priceCache.GetRecentClosesAsync("SPX", 32, date, cancellation);
			if (closes.Count > 0)
			{
				prevClose ??= closes[^1];
				hv3OverHv30 = ComputeHvRatio(closes, shortWindow: 3, longWindow: 30);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Log.Debug($"CascadeScreener close lookup failed for {ticker}: {ex.Message}");
		}

		decimal? vixPriorClose = null;
		decimal? maxPriorVix9dOverVix = null;
		try
		{
			var vixCloses = await priceCache.GetRecentClosesAsync("VIX", 4, date, cancellation);
			var vix9dCloses = await priceCache.GetRecentClosesAsync("VIX9D", 4, date, cancellation);
			if (vixCloses.Count > 0)
				vixPriorClose = vixCloses[^1];
			var pairCount = Math.Min(3, Math.Min(vixCloses.Count, vix9dCloses.Count));
			for (var i = 1; i <= pairCount; i++)
			{
				var v = vixCloses[^i];
				var v9 = vix9dCloses[^i];
				if (v <= 0m) continue;
				var ratio = (v9 - v) / v;
				if (!maxPriorVix9dOverVix.HasValue || ratio > maxPriorVix9dOverVix.Value)
					maxPriorVix9dOverVix = ratio;
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Log.Debug($"CascadeScreener VIX/VIX9D lookup failed: {ex.Message}");
		}

		// 4. Underlying RTH session tape up to asOfEt
		var rthBars = preloadedSessionRthBars;
		if (rthBars == null || rthBars.Count == 0)
			rthBars = await LoadSessionRthBarsAsync(ticker, asOfEt, intradayCache, includeCurrentBar, cancellation);
		var tape = rthBars.Count > 0
			? SessionTape.Build(rthBars, prevClose, minGapPct: 0.15m, rangeWindowMinutes: 25, boundaryTouchTolerancePct: 0.08m)
			: null;
		if (tape != null && spot > 0m && Math.Abs(tape.Spot - spot) > 0.01m)
			tape = tape with { Spot = spot };

		// 5. Intraday VIX open & current up to asOfEt
		decimal? vixOpen = null, vixNow = null;
		var vixBars = await LoadSessionRthBarsAsync("VIX", asOfEt, intradayCache, includeCurrentBar, cancellation);
		if (vixBars.Count > 0)
		{
			vixOpen = vixBars[0].Open > 0m ? vixBars[0].Open : vixBars[0].Close;
			vixNow = vixBars[^1].Close;
		}

		// 6. Settled CBOE SMILE index, 1-day change, and stationary 20-day z-score (strictly before `date`)
		decimal? smileIndex = null, smileChange1d = null, smileZScore20d = null;
		try
		{
			var sc = smileCache ?? new SmileIndexCache(offline: true);
			(smileIndex, smileChange1d, smileZScore20d) = await sc.GetTrailingStatsAsync(date, window: 20, cancellation);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Log.Debug($"CascadeScreener SMILE lookup failed: {ex.Message}");
		}

		return new CascadeInputs(
			FlowShare: flowShare,
			Volume: volumePace,
			DeltaOiShare: deltaOiShare,
			PriorOiDate: priorOiDate,
			Tape: tape,
			VixPriorClose: vixPriorClose,
			VixOpen: vixOpen,
			VixNow: vixNow,
			MaxPriorVix9dOverVix: maxPriorVix9dOverVix,
			Hv3OverHv30: hv3OverHv30,
			SmileIndex: smileIndex,
			SmileChange1d: smileChange1d,
			SmileZScore20d: smileZScore20d);
	}

	/// <summary>Loads RTH (09:30–16:00 ET) 1-minute bars for <paramref name="ticker"/> on <paramref name="asOfEt"/>'s date up to <paramref name="asOfEt"/>.</summary>
	internal static async Task<IReadOnlyList<MinuteBar>> LoadSessionRthBarsAsync(
		string ticker,
		DateTime asOfEt,
		IntradayBarCache? intradayCache,
		bool includeCurrentBar,
		CancellationToken cancellation)
	{
		var date = asOfEt.Date;
		var tod = asOfEt.TimeOfDay;
		if (tod < AnalyzeGexSettings.RthOpen) return Array.Empty<MinuteBar>();

		if (intradayCache != null)
		{
			try
			{
				var openEt = new DateTime(date.Year, date.Month, date.Day, 9, 30, 0, DateTimeKind.Unspecified);
				var endEt = new DateTime(date.Year, date.Month, date.Day, Math.Min(16, asOfEt.Hour), asOfEt.Hour >= 16 ? 0 : asOfEt.Minute, asOfEt.Hour >= 16 ? 0 : asOfEt.Second, DateTimeKind.Unspecified);
				var fromUtc = MarketTime.EtToInstant(openEt);
				var toUtc = includeCurrentBar ? MarketTime.EtToInstant(endEt) : MarketTime.EtToInstant(endEt).AddTicks(-1);
				if (fromUtc <= toUtc)
				{
					var bars = await intradayCache.GetBarsAsync(ticker, fromUtc, toUtc, BarInterval.M1, includeExtended: false, cancellation);
					var rth = FilterRthBars(bars, tod, includeCurrentBar);
					if (rth.Count > 0) return rth;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				Log.Debug($"CascadeScreener intradayCache failed for {ticker}: {ex.Message}");
			}
		}

		var disk = LoadRthBarsFromDisk(ticker, date, tod, includeCurrentBar);
		if (disk.Count == 0 && string.Equals(ticker, "SPXW", StringComparison.OrdinalIgnoreCase))
			disk = LoadRthBarsFromDisk("SPX", date, tod, includeCurrentBar);
		return disk;
	}

	/// <summary>Reads <c>data/intraday/&lt;TICKER&gt;/&lt;date&gt;.csv</c> directly when no <see cref="IntradayBarCache"/> is wired.</summary>
	internal static IReadOnlyList<MinuteBar> LoadRthBarsFromDisk(string ticker, DateTime date, TimeSpan maxTimeEt, bool includeCurrentBar = true)
	{
		var path = Program.ResolvePath($"data/intraday/{ticker.ToUpperInvariant()}/{date:yyyy-MM-dd}.csv");
		if (!File.Exists(path)) return Array.Empty<MinuteBar>();
		var raw = new List<MinuteBar>();
		foreach (var line in File.ReadLines(path).Skip(1))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			if (parts.Length < 6) continue;
			if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var open)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var high)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var low)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var close)) continue;
			long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var vol);
			raw.Add(new MinuteBar(ts, open, high, low, close, vol));
		}
		return FilterRthBars(raw, maxTimeEt, includeCurrentBar);
	}

	private static IReadOnlyList<MinuteBar> FilterRthBars(IReadOnlyList<MinuteBar> bars, TimeSpan maxTimeEt, bool includeCurrentBar)
	{
		var rthOpen = AnalyzeGexSettings.RthOpen;
		var rthClose = AnalyzeGexSettings.RthClose;
		var list = new List<MinuteBar>();
		foreach (var b in bars)
		{
			var et = MarketTime.ToEt(b.Timestamp);
			var tod = et.TimeOfDay;
			if (tod < rthOpen || tod >= rthClose) continue;
			if (includeCurrentBar ? tod > maxTimeEt : tod >= maxTimeEt) continue;
			list.Add(b);
		}
		list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return list;
	}

	private static decimal? ComputeHvRatio(IReadOnlyList<decimal> closes, int shortWindow, int longWindow)
	{
		if (closes.Count < shortWindow + 1 || closes.Count < 10) return null;
		var returns = new List<double>(closes.Count - 1);
		for (var i = 1; i < closes.Count; i++)
		{
			if (closes[i - 1] <= 0m || closes[i] <= 0m) continue;
			returns.Add(Math.Log((double)(closes[i] / closes[i - 1])));
		}
		if (returns.Count < shortWindow) return null;

		static double StdDev(IReadOnlyList<double> r, int count)
		{
			var n = Math.Min(count, r.Count);
			if (n < 2) return 0.0;
			var slice = r.Skip(r.Count - n).ToList();
			var mean = slice.Average();
			var sumSq = slice.Sum(x => (x - mean) * (x - mean));
			return Math.Sqrt(sumSq / (n - 1));
		}

		var shortVol = StdDev(returns, shortWindow);
		var longVol = StdDev(returns, Math.Min(longWindow, returns.Count));
		if (longVol <= 1e-8) return null;
		return (decimal)(shortVol / longVol);
	}
}
