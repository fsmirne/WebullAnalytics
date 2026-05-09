using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class DirectionalFitTests
{
	[Theory]
	[InlineData(OpenStructureKind.LongCall, 1)]
	[InlineData(OpenStructureKind.ShortPutVertical, 1)]
	[InlineData(OpenStructureKind.LongPut, -1)]
	[InlineData(OpenStructureKind.ShortCallVertical, -1)]
	[InlineData(OpenStructureKind.LongCalendar, 0)]
	[InlineData(OpenStructureKind.LongDiagonal, 0)]
	public void FitSignMatchesSpecTable(OpenStructureKind kind, int expected)
	{
		Assert.Equal(expected, DirectionalFit.SignFor(kind));
	}

	private static CandidateSkeleton DiagonalSkel(string callPut, decimal longStrike, decimal shortStrike)
	{
		var shortExp = new DateTime(2026, 5, 8);
		var longExp = new DateTime(2026, 5, 29);
		return new CandidateSkeleton("SPY", OpenStructureKind.LongDiagonal, new[]
		{
			new ProposalLeg("sell", MatchKeys.OccSymbol("SPY", shortExp, shortStrike, callPut), 1),
			new ProposalLeg("buy", MatchKeys.OccSymbol("SPY", longExp, longStrike, callPut), 1)
		}, TargetExpiry: shortExp);
	}

	[Fact]
	public void LongCallDiagonalIsBullishWhenLongStrikeIsBelowShort()
	{
		Assert.Equal(1, DirectionalFit.SignFor(DiagonalSkel("C", longStrike: 500m, shortStrike: 505m)));
	}

	[Fact]
	public void LongCallDiagonalIsBearishWhenLongStrikeIsAboveShort()
	{
		Assert.Equal(-1, DirectionalFit.SignFor(DiagonalSkel("C", longStrike: 510m, shortStrike: 505m)));
	}

	[Fact]
	public void LongPutDiagonalIsBearishWhenLongStrikeIsAboveShort()
	{
		Assert.Equal(-1, DirectionalFit.SignFor(DiagonalSkel("P", longStrike: 505m, shortStrike: 500m)));
	}

	[Fact]
	public void LongPutDiagonalIsBullishWhenLongStrikeIsBelowShort()
	{
		Assert.Equal(1, DirectionalFit.SignFor(DiagonalSkel("P", longStrike: 495m, shortStrike: 500m)));
	}

	[Fact]
	public void DiagonalWithEqualStrikesFallsBackToNeutral()
	{
		Assert.Equal(0, DirectionalFit.SignFor(DiagonalSkel("C", longStrike: 500m, shortStrike: 500m)));
	}
}
