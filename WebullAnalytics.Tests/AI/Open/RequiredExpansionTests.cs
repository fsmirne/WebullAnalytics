using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class RequiredExpansionTests
{
	private static OpenerConfig CleanConfig()
	{
		var cfg = new OpenerConfig();
		cfg.Structures.LongCalendar.Enabled = false;
		cfg.Structures.DoubleCalendar.Enabled = false;
		cfg.Structures.LongDiagonal.Enabled = false;
		cfg.Structures.DoubleDiagonal.Enabled = false;
		cfg.Structures.IronButterfly.Enabled = false;
		cfg.Structures.IronCondor.Enabled = false;
		cfg.Structures.Condor.Enabled = false;
		cfg.Structures.ShortVertical.Enabled = false;
		cfg.Structures.LongCallPut.Enabled = false;
		cfg.Structures.LongVertical.Enabled = false;
		cfg.Structures.DiagonalVertical.Enabled = false;
		cfg.Structures.CalendarVertical.Enabled = false;
		cfg.Structures.GravityFly.Enabled = false;
		return cfg;
	}

	[Fact]
	public void ZeroDte_ProducesCompactExpansion()
	{
		var cfg = CleanConfig();
		cfg.Structures.ShortVertical.Enabled = true;
		cfg.Structures.ShortVertical.ShortDeltaMin = 0.10m;
		cfg.Structures.ShortVertical.WidthSteps = new List<int> { 2, 4 };

		// SPXW 0DTE: spot 5500, timeYears 0.00074, iv 0.15, step 5.0
		var expand = cfg.ComputeRequiredExpansionPct(5500m, timeYears: 0.0, iv: 0.15m, strikeStep: 5.0m, riskFreeRate: 0.043);
		// Should be floored at 5% or tightly bounded (< 10%)
		Assert.True(expand >= 0.05m && expand <= 0.10m, $"Expected 0DTE expansion between 5% and 10%, got {expand:P1}");
	}

	[Fact]
	public void MultiWeek_ShortVertical_ProducesAppropriateExpansion()
	{
		var cfg = CleanConfig();
		cfg.Structures.ShortVertical.Enabled = true;
		cfg.Structures.ShortVertical.ShortDeltaMin = 0.15m;
		cfg.Structures.ShortVertical.WidthSteps = new List<int> { 2, 4, 6, 8, 10 };

		// AAPL 50DTE: spot 270, timeYears 50/365, iv 0.25, step 5.0
		var expand = cfg.ComputeRequiredExpansionPct(270m, timeYears: 50.0 / 365.0, iv: 0.25m, strikeStep: 5.0m, riskFreeRate: 0.043);
		// 15 delta + 10 width steps on 50DTE AAPL reaches ~25-30% OTM, with 25% margin gives ~30-40%
		Assert.True(expand >= 0.25m && expand <= 0.50m, $"Expected 50DTE expansion between 25% and 50%, got {expand:P1}");
	}

	[Fact]
	public void HighVol_Ticker_ExpandsAppropriately()
	{
		var cfg = CleanConfig();
		cfg.Structures.ShortVertical.Enabled = true;
		cfg.Structures.ShortVertical.ShortDeltaMin = 0.20m;
		cfg.Structures.ShortVertical.WidthSteps = new List<int> { 2, 4 };

		// GME 30DTE with 80% IV: spot 25, timeYears 30/365, iv 0.80, step 1.0
		var expand = cfg.ComputeRequiredExpansionPct(25m, timeYears: 30.0 / 365.0, iv: 0.80m, strikeStep: 1.0m, riskFreeRate: 0.043);
		Assert.True(expand >= 0.35m && expand <= 0.70m, $"Expected high-vol expansion between 35% and 70%, got {expand:P1}");
	}
}
