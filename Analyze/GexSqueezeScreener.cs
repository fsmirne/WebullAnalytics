using WebullAnalytics.Pricing;

namespace WebullAnalytics.Analyze;

internal enum SqueezeSide
{
	Bullish,
	Bearish,
}

internal enum SetupMark
{
	Pass,
	Warn,
	Fail,
	Unknown,
}

/// <summary>One scored factor. Points is null when the input was unavailable; the factor then drops out of the
/// score's denominator instead of counting as zero.</summary>
internal sealed record SqueezeFactor(string Name, int Max, int? Points, SetupMark Mark, string Note);

/// <summary>The gamma terrain the screener reads: spot, the chain-wide gamma flip ("trigger"), whether spot sits in the
/// short-gamma zone, the chain-wide call/put walls, and a one-trading-day expected move used to scale wall distance.</summary>
internal sealed record SqueezeTerrain(decimal Spot, decimal? Trigger, bool ShortGamma, decimal? CallWall, decimal? PutWall, decimal? DailyMove);

/// <summary>Recent volume pace vs the session's average pace, from two data/iv captures (see <see cref="GexSqueezeScreener.VolumePaceFrom"/>).</summary>
internal sealed record VolumePace(decimal Ratio, TimeSpan ReferenceTs, TimeSpan AnchorTs);

/// <summary>The flow-side inputs. FlowShare and DeltaOiShare are delta-weighted call-minus-put shares in [−1, 1].</summary>
internal sealed record SqueezeInputs(decimal? FlowShare, VolumePace? Volume, decimal? DeltaOiShare, DateTime? PriorOiDate);

/// <summary>Score is points ÷ the max of the factors that had data, scaled to 100; Points/Possible and the factor counts
/// expose that coverage so a 100 built from four factors is not read as a 100 built from five.</summary>
internal sealed record SqueezeReading(SqueezeSide Side, int Score, string Band, SqueezeTerrain Terrain, IReadOnlyList<SqueezeFactor> Factors)
{
	public decimal? Wall => Side == SqueezeSide.Bullish ? Terrain.CallWall : Terrain.PutWall;
	public int Points => Factors.Sum(f => f.Points ?? 0);
	public int Possible => Factors.Where(f => f.Points.HasValue).Sum(f => f.Max);
	public int FactorsScored => Factors.Count(f => f.Points.HasValue);
	public bool Complete => FactorsScored == Factors.Count;
}

/// <summary>
/// `analyze gex --view squeeze`: a five-factor gamma-squeeze score (weights 25/25/25/20/5). Both sides are scored and the
/// higher one is shown, so the bias is simply the side whose setup scores better:
/// <list type="bullet">
/// <item><description>Gamma regime (25) — how far spot sits below the gamma flip (dealers net short gamma, hedging amplifies moves), ramped linearly over ±<see cref="RegimeRampMoves"/> daily moves so crossing the flip by pennies moves the score a few points, not 25.</description></item>
/// <item><description>Wall proximity (25) — distance to the call wall (bullish) / put wall (bearish) ahead of spot, in daily expected moves.</description></item>
/// <item><description>Flow alignment (25) — delta-weighted call-vs-put day volume. Volume is unsigned: it says which side traded, not who bought.</description></item>
/// <item><description>Volume confirm (20) — the last ~15 minutes' contract pace vs the session average, from data/iv captures.</description></item>
/// <item><description>ΔOI alignment (5) — delta-weighted call-vs-put open-interest change since the prior data/oi snapshot.</description></item>
/// </list>
/// A factor without data is dropped from the denominator, and an incomplete reading is capped at "Likely" so a missing
/// factor cannot produce "Imminent". Our own studies found GEX terrain predicts range, not direction (see the project's GEX research notes), so read the
/// bias as a description of the book, not a forecast.
/// </summary>
internal static class GexSqueezeScreener
{
	internal const int RegimeMax = 25, WallMax = 25, FlowMax = 25, VolumeMax = 20, DeltaOiMax = 5;
	/// <summary>|delta-weighted call share| below this is "neutral" flow, which scores <see cref="FlowNeutralPoints"/>.</summary>
	internal const decimal FlowNeutralBand = 0.15m;
	internal const int FlowNeutralPoints = 10;
	/// <summary>Volume pace scores zero at or below <see cref="VolumeZeroRatio"/>× the session average, full at or above
	/// <see cref="VolumeFullRatio"/>×; at or above <see cref="VolumeAcceleratingRatio"/>× it reads as accelerating.</summary>
	internal const decimal VolumeZeroRatio = 1m, VolumeFullRatio = 2m, VolumeAcceleratingRatio = 1.5m;
	/// <summary>Lower bounds of the Possible / Likely / Imminent bands.</summary>
	internal const int BandPossible = 30, BandLikely = 50, BandImminent = 75;
	/// <summary>Wall proximity scores full at or inside this many daily moves and zero at or beyond <see cref="WallZeroMoves"/>.</summary>
	internal const decimal WallFullMoves = 0.5m, WallZeroMoves = 3m;
	/// <summary>Gamma regime scores full this many daily moves below the flip, zero this many above, and half at the flip.</summary>
	internal const decimal RegimeRampMoves = 0.5m;
	/// <summary>Minimum spacing between the two captures the volume pace compares.</summary>
	internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

	public static SqueezeReading Evaluate(SqueezeTerrain terrain, SqueezeInputs inputs)
	{
		var bull = Score(SqueezeSide.Bullish, terrain, inputs);
		var bear = Score(SqueezeSide.Bearish, terrain, inputs);
		if (bull.Score != bear.Score) return bull.Score > bear.Score ? bull : bear;
		return (WallDistance(SqueezeSide.Bullish, terrain) ?? decimal.MaxValue) <= (WallDistance(SqueezeSide.Bearish, terrain) ?? decimal.MaxValue) ? bull : bear;
	}

	/// <summary>The band for a score, capped at "Likely" when a factor had no data.</summary>
	public static string Band(int score, bool complete) => !complete && score >= BandImminent ? "Likely" : Band(score);

	public static string Band(int score) => score switch
	{
		< BandPossible => "Unlikely",
		< BandLikely => "Possible",
		< BandImminent => "Likely",
		_ => "Imminent",
	};

	/// <summary>Builds the terrain from a gamma matrix: chain-wide flip and walls, short-gamma when spot is below the flip
	/// (or, with no flip in range, when net call−put gamma at spot is negative), and a one-trading-day expected move from
	/// the IV at the strike(s) nearest spot on the nearest expiry after <paramref name="asOf"/>'s date. The same-day expiry is
	/// skipped: its IV is either vendor-reported intraday noise or, on a data/oi replay, back-solved from EOD mids at a T
	/// measured from midnight — 0.09% "daily moves" on SPXW.</summary>
	public static SqueezeTerrain TerrainFrom(GexMatrix matrix, decimal spot, DateTime asOf)
	{
		var flip = matrix.FindGammaFlip(spot);
		var shortGamma = flip.HasValue ? spot < flip.Value : matrix.TotalCallGex - matrix.TotalPutGex < 0m;
		var (callWall, putWall) = matrix.FindChainWalls();
		var front = matrix.Expiries.Cast<DateTime?>().FirstOrDefault(e => e > asOf.Date) ?? matrix.Expiries.Cast<DateTime?>().FirstOrDefault();
		var atm = matrix.Contributors.Where(c => c.Expiry == front).OrderBy(c => Math.Abs(c.Strike - spot)).Take(2).ToList();
		decimal? dailyMove = atm.Count > 0 ? spot * atm.Average(c => c.Iv) * (decimal)Math.Sqrt(1.0 / 252.0) : null;
		return new SqueezeTerrain(spot, flip, shortGamma, callWall, putWall, dailyMove);
	}

	/// <summary>Delta-weighted call share of day volume: (Σcall vol·|Δ| − Σput vol·|Δ|) / (sum of both). Null with no volume.</summary>
	public static decimal? FlowShare(IEnumerable<GexContributor> contributors, decimal spot) =>
		Share(contributors.Select(c => (c, (decimal)c.Volume)), spot);

	/// <summary>Delta-weighted call share of the open-interest CHANGE since a prior snapshot, signed: (Σcall ΔOI·|Δ| − Σput ΔOI·|Δ|) /
	/// Σ|ΔOI|·|Δ|. Contracts missing from the prior snapshot are skipped, not read as fresh listings — the live scraper's
	/// snapshots carry only the 0DTE chain until the nightly backfill completes them, so absence is not evidence of zero.</summary>
	public static decimal? DeltaOiShare(IEnumerable<GexContributor> contributors, decimal spot, IReadOnlyDictionary<(DateTime Expiry, decimal Strike, bool IsCall), long> priorOi) =>
		Share(contributors.Where(c => priorOi.ContainsKey((c.Expiry, c.Strike, c.IsCall))).Select(c => (c, (decimal)(c.Oi - priorOi[(c.Expiry, c.Strike, c.IsCall)]))), spot);

	private static decimal? Share(IEnumerable<(GexContributor C, decimal Amount)> rows, decimal spot)
	{
		decimal net = 0m, gross = 0m;
		foreach (var (c, amount) in rows)
		{
			var weighted = amount * Math.Abs(OptionMath.Delta(spot, c.Strike, c.TimeYears, OptionMath.RiskFreeRate, c.Iv, c.IsCall ? "C" : "P"));
			net += c.IsCall ? weighted : -weighted;
			gross += Math.Abs(weighted);
		}
		return gross > 0m ? net / gross : null;
	}

	/// <summary>Recent-vs-session volume pace from cumulative-volume captures: the anchor is the latest capture, the reference
	/// the latest one at least <see cref="RecentWindow"/> earlier. Both are summed over the contracts present in both captures so
	/// a capture with a wider strike window cannot fake a burst. Ratio = (Δvolume / Δminutes) / (anchor volume / minutes since
	/// <paramref name="sessionOpen"/>). Null without two qualifying captures or with no volume at the anchor.</summary>
	public static VolumePace? VolumePaceFrom<TKey>(SortedDictionary<TimeSpan, Dictionary<TKey, long>> captures, TimeSpan sessionOpen) where TKey : notnull
	{
		var times = captures.Keys.Where(t => t > sessionOpen).ToList();
		if (times.Count < 2) return null;
		var anchorTs = times[^1];
		var refTs = times.LastOrDefault(t => t <= anchorTs - RecentWindow);
		if (refTs == default) return null;
		var anchor = captures[anchorTs];
		var reference = captures[refTs];
		long anchorVol = 0, refVol = 0;
		foreach (var (key, vol) in anchor)
		{
			if (!reference.TryGetValue(key, out var prior)) continue;
			anchorVol += vol;
			refVol += prior;
		}
		if (anchorVol <= 0) return null;
		var recentRate = Math.Max(0m, anchorVol - refVol) / (decimal)(anchorTs - refTs).TotalMinutes;
		var sessionRate = anchorVol / (decimal)(anchorTs - sessionOpen).TotalMinutes;
		return new VolumePace(recentRate / sessionRate, refTs, anchorTs);
	}

	private static decimal? WallDistance(SqueezeSide side, SqueezeTerrain t)
	{
		var wall = side == SqueezeSide.Bullish ? t.CallWall : t.PutWall;
		if (!wall.HasValue) return null;
		return side == SqueezeSide.Bullish ? wall.Value - t.Spot : t.Spot - wall.Value;
	}

	private static SqueezeReading Score(SqueezeSide side, SqueezeTerrain t, SqueezeInputs inputs)
	{
		var factors = new List<SqueezeFactor> { RegimeFactor(t), WallFactor(side, t), FlowFactor(side, inputs.FlowShare), VolumeFactor(inputs.Volume), DeltaOiFactor(side, inputs.DeltaOiShare, inputs.PriorOiDate) };
		var available = factors.Where(f => f.Points.HasValue).ToList();
		var max = available.Sum(f => f.Max);
		var score = max > 0 ? (int)Math.Round(100m * available.Sum(f => f.Points!.Value) / max, MidpointRounding.AwayFromZero) : 0;
		return new SqueezeReading(side, score, Band(score, available.Count == factors.Count), t, factors);
	}

	/// <summary>Proportional when both the flip and a daily move exist: depth below the flip in daily moves, ramped over
	/// ±<see cref="RegimeRampMoves"/>. Without them it falls back to the all-or-nothing sign (no distance to scale).</summary>
	private static SqueezeFactor RegimeFactor(SqueezeTerrain t)
	{
		const string name = "Gamma Regime";
		if (!t.Trigger.HasValue || t.DailyMove is not > 0m)
		{
			var why = t.Trigger.HasValue ? $" — no ATM IV to scale the distance to the ${t.Trigger.Value:N2} flip" : " — no flip in range, read from net gamma at spot";
			return t.ShortGamma
				? new SqueezeFactor(name, RegimeMax, RegimeMax, SetupMark.Pass, $"Short gamma environment (amplifies moves){why}")
				: new SqueezeFactor(name, RegimeMax, 0, SetupMark.Fail, $"Long gamma environment (dampens squeeze){why}");
		}
		var depth = (t.Trigger.Value - t.Spot) / t.DailyMove.Value;   // + = below the flip (short gamma)
		var points = (int)Math.Round(RegimeMax * Math.Clamp((depth + RegimeRampMoves) / (2m * RegimeRampMoves), 0m, 1m), MidpointRounding.AwayFromZero);
		var where = $"spot {Math.Abs(depth):F2} daily moves {(depth >= 0m ? "below" : "above")} the ${t.Trigger.Value:N2} flip";
		if (points * 3 >= RegimeMax * 2) return new SqueezeFactor(name, RegimeMax, points, SetupMark.Pass, $"Short gamma environment (amplifies moves) — {where}");
		if (points * 3 <= RegimeMax) return new SqueezeFactor(name, RegimeMax, points, SetupMark.Fail, $"Long gamma environment (dampens squeeze) — {where}");
		return new SqueezeFactor(name, RegimeMax, points, SetupMark.Warn, $"Near the gamma flip, regime unsettled — {where}");
	}

	private static SqueezeFactor WallFactor(SqueezeSide side, SqueezeTerrain t)
	{
		var name = side == SqueezeSide.Bullish ? "Call Wall Proximity" : "Put Wall Proximity";
		var label = side == SqueezeSide.Bullish ? "Call wall" : "Put wall";
		var wall = side == SqueezeSide.Bullish ? t.CallWall : t.PutWall;
		var distance = WallDistance(side, t);
		if (!wall.HasValue || !distance.HasValue) return new SqueezeFactor(name, WallMax, null, SetupMark.Unknown, $"No {label.ToLowerInvariant()} in the window");
		if (distance.Value <= 0m) return new SqueezeFactor(name, WallMax, 0, SetupMark.Fail, $"Price already through the {label.ToLowerInvariant()} at ${wall.Value:N2}");
		if (t.DailyMove is not > 0m) return new SqueezeFactor(name, WallMax, null, SetupMark.Unknown, $"{label} at ${wall.Value:N2} (no ATM IV to scale the distance)");
		var moves = distance.Value / t.DailyMove.Value;
		var points = (int)Math.Round(WallMax * Math.Clamp((WallZeroMoves - moves) / (WallZeroMoves - WallFullMoves), 0m, 1m), MidpointRounding.AwayFromZero);
		return new SqueezeFactor(name, WallMax, points, points >= WallMax / 2 ? SetupMark.Pass : SetupMark.Warn, $"{label} at ${wall.Value:N2} ({moves:F1} daily moves away)");
	}

	private static SqueezeFactor FlowFactor(SqueezeSide side, decimal? share)
	{
		const string name = "Flow Alignment";
		if (!share.HasValue) return new SqueezeFactor(name, FlowMax, null, SetupMark.Unknown, "Flow n/a — the chain carries no day volume");
		var sideWord = side == SqueezeSide.Bullish ? "bullish" : "bearish";
		var signed = side == SqueezeSide.Bullish ? share.Value : -share.Value;
		var tag = $"(Δ-weighted call share {share.Value:+0.00;-0.00})";
		if (signed >= FlowNeutralBand) return new SqueezeFactor(name, FlowMax, FlowMax, SetupMark.Pass, $"{Capitalize(sideWord)} flow {tag}");
		if (signed <= -FlowNeutralBand) return new SqueezeFactor(name, FlowMax, 0, SetupMark.Fail, $"Flow opposes the {sideWord} setup {tag}");
		return new SqueezeFactor(name, FlowMax, FlowNeutralPoints, SetupMark.Warn, $"Neutral flow — {sideWord} flow would strengthen {tag}");
	}

	private static SqueezeFactor VolumeFactor(VolumePace? pace)
	{
		const string name = "Volume Confirm";
		if (pace == null) return new SqueezeFactor(name, VolumeMax, null, SetupMark.Unknown, $"Volume pace n/a — needs two data/iv captures ≥{RecentWindow.TotalMinutes:F0} min apart (re-run, or keep wa-scraper running)");
		var points = (int)Math.Round(VolumeMax * Math.Clamp((pace.Ratio - VolumeZeroRatio) / (VolumeFullRatio - VolumeZeroRatio), 0m, 1m), MidpointRounding.AwayFromZero);
		var window = $"{pace.ReferenceTs:hh\\:mm}→{pace.AnchorTs:hh\\:mm} vs session";
		if (pace.Ratio >= VolumeAcceleratingRatio) return new SqueezeFactor(name, VolumeMax, points, SetupMark.Pass, $"Visible flow accelerating ({pace.Ratio:F2}x recent, {window})");
		if (pace.Ratio >= VolumeZeroRatio) return new SqueezeFactor(name, VolumeMax, points, SetupMark.Warn, $"Flow pace steady ({pace.Ratio:F2}x recent, {window})");
		return new SqueezeFactor(name, VolumeMax, points, SetupMark.Fail, $"Flow decelerating ({pace.Ratio:F2}x recent, {window})");
	}

	private static SqueezeFactor DeltaOiFactor(SqueezeSide side, decimal? share, DateTime? priorDate)
	{
		const string name = "Delta OI Alignment";
		if (!share.HasValue || !priorDate.HasValue) return new SqueezeFactor(name, DeltaOiMax, null, SetupMark.Unknown, "ΔOI n/a — no prior data/oi snapshot overlapping this window");
		var aligned = side == SqueezeSide.Bullish ? share.Value > 0m : share.Value < 0m;
		var lean = share.Value > 0m ? "calls" : share.Value < 0m ? "puts" : "neither side";
		return new SqueezeFactor(name, DeltaOiMax, aligned ? DeltaOiMax : 0, aligned ? SetupMark.Pass : SetupMark.Fail, $"OI build since {priorDate.Value:MM-dd} favors {lean} ({share.Value:+0.00;-0.00})");
	}

	private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
