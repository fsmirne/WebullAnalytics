using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open.ZeroDte;

// Config validation for opener.zeroDteGate. The pairs worth guarding are the ones whose inversion produces
// a gate that can never fire — an empty entry window, an empty strike-distance band, an empty credit band.
// Those fail silently as "no trades ever" rather than loudly, which is the worst failure mode for a gate
// whose whole job is to sometimes say no.
public class ZeroDteGateConfigValidationTests
{
	private static AIConfig Cfg()
	{
		var c = new AIConfig { Ticker = "SPXW", Indicators = new IndicatorsConfig { IvDefaultPct = 0.15m, StrikeStep = 5m } };
		c.Opener.Indicators = c.Indicators;
		c.Opener.ZeroDteGate.Enabled = true;
		return c;
	}

	[Fact]
	public void DefaultGateConfigIsValid()
		=> Assert.Null(AIConfigLoader.Validate(Cfg()));

	[Fact]
	public void UnknownDirectionModeIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Direction.Mode = "vibes";
		Assert.Contains("zeroDteGate.direction.mode", AIConfigLoader.Validate(c) ?? "");
	}

	[Theory]
	[InlineData("gapAndVwap")]
	[InlineData("vwapOnly")]
	[InlineData("netChange")]
	[InlineData("composite")]
	[InlineData("off")]
	public void EveryDocumentedDirectionModeIsAccepted(string mode)
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Direction.Mode = mode;
		Assert.Null(AIConfigLoader.Validate(c));
	}

	[Fact]
	public void UnknownLevelSourceIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Placement.LevelSource = "either";   // renamed to "both"; the old name must fail loudly
		Assert.Contains("zeroDteGate.placement.levelSource", AIConfigLoader.Validate(c) ?? "");
	}

	[Theory]
	[InlineData("gexWall")]
	[InlineData("rangeBoundary")]
	[InlineData("both")]
	public void EveryDocumentedLevelSourceIsAccepted(string source)
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Placement.LevelSource = source;
		Assert.Null(AIConfigLoader.Validate(c));
	}

	[Fact]
	public void InvertedEntryWindowIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.EarliestEntryEt = "13:00";
		c.Opener.ZeroDteGate.LatestEntryEt = "10:15";
		Assert.Contains("must be later than earliestEntryEt", AIConfigLoader.Validate(c) ?? "");
	}

	[Fact]
	public void MalformedEntryTimeIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.EarliestEntryEt = "quarter past ten";
		Assert.Contains("zeroDteGate.earliestEntryEt", AIConfigLoader.Validate(c) ?? "");
	}

	[Fact]
	public void InvertedShortDistanceBandIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Placement.MinShortDistancePct = 1.0m;
		c.Opener.ZeroDteGate.Placement.MaxShortDistancePct = 0.5m;
		Assert.Contains("must exceed minShortDistancePct", AIConfigLoader.Validate(c) ?? "");
	}

	[Fact]
	public void InvertedCreditBandIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Credit.MinPctOfWidth = 0.40m;
		c.Opener.ZeroDteGate.Credit.MaxPctOfWidth = 0.20m;
		Assert.Contains("must exceed minPctOfWidth", AIConfigLoader.Validate(c) ?? "");
	}

	/// <summary>0 disables each bound rather than meaning "reject everything", so the disabled combinations
	/// must validate.</summary>
	[Fact]
	public void ZeroBoundsDisableRatherThanInvert()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Placement.MinShortDistancePct = 0m;
		c.Opener.ZeroDteGate.Placement.MaxShortDistancePct = 0m;
		c.Opener.ZeroDteGate.Placement.MaxLevelDistancePct = 0m;
		c.Opener.ZeroDteGate.Credit.MinPctOfWidth = 0m;
		c.Opener.ZeroDteGate.Credit.MaxPctOfWidth = 0m;
		c.Opener.ZeroDteGate.EarliestEntryEt = null;
		c.Opener.ZeroDteGate.LatestEntryEt = null;
		Assert.Null(AIConfigLoader.Validate(c));
	}

	[Fact]
	public void NegativeHoldMinutesIsRejected()
	{
		var c = Cfg();
		c.Opener.ZeroDteGate.Direction.MinVwapHoldMinutes = -1;
		Assert.Contains("zeroDteGate.direction.minVwapHoldMinutes", AIConfigLoader.Validate(c) ?? "");
	}
}
