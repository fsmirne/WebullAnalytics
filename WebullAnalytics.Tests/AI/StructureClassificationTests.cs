using WebullAnalytics;
using WebullAnalytics.AI.RiskDiagnostics;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class StructureClassificationTests
{
	// ParsingHelpers.ClassifyStrategyKind(legCount, distinctExpiries, distinctStrikes, distinctCallPut).
	[Theory]
	[InlineData(4, 2, 2, 1, "CalendarVertical")]   // single-sided, 2 expiries, same anchor+wing -> calendar
	[InlineData(4, 2, 3, 1, "DiagonalVertical")]   // single-sided, offset anchors -> diagonal
	[InlineData(4, 2, 4, 1, "DiagonalVertical")]
	[InlineData(4, 2, 2, 2, "DoubleCalendar")]     // two-sided regression — unchanged
	[InlineData(4, 2, 3, 2, "DoubleDiagonal")]
	[InlineData(4, 1, 3, 2, "IronButterfly")]
	public void ClassifiesFourLeggers(int legs, int expiries, int strikes, int callPut, string expected)
		=> Assert.Equal(expected, ParsingHelpers.ClassifyStrategyKind(legs, expiries, strikes, callPut));

	private static DiagnosticLeg Leg(DateTime exp, decimal strike, bool isLong, decimal price) =>
		new(Symbol: $"SPY{exp:yyMMdd}C{strike}", Parsed: new OptionParsed("SPY", exp, "C", strike), IsLong: isLong, Qty: 100, PricePerShare: price, CostBasisPerShare: null);

	[Fact]
	public void RiskBuilderLabelsSameAnchorAsCalendarVertical()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 27);
		var longExp = new DateTime(2026, 5, 15);
		// Same anchor (50) + same wing (52) on both expiries.
		var legs = new[]
		{
			Leg(longExp, 50m, isLong: true, 2.0m),
			Leg(longExp, 52m, isLong: false, 1.0m),
			Leg(shortExp, 50m, isLong: false, 1.5m),
			Leg(shortExp, 52m, isLong: true, 0.5m),
		};
		var diag = RiskDiagnosticBuilder.Build(legs, spot: 50m, asOf, ivResolver: _ => 0.40m, trend: null);
		Assert.Equal("calendar_vertical", diag.StructureLabel);
		Assert.Equal("neutral", diag.DirectionalBias);
	}

	[Fact]
	public void RiskBuilderLabelsOffsetAnchorsAsDiagonalVertical()
	{
		var asOf = new DateTime(2026, 4, 20);
		var shortExp = new DateTime(2026, 4, 27);
		var longExp = new DateTime(2026, 5, 15);
		// Offset anchors (long 50, short 53) -> four distinct strikes -> diagonal.
		var legs = new[]
		{
			Leg(longExp, 50m, isLong: true, 2.0m),
			Leg(longExp, 52m, isLong: false, 1.0m),
			Leg(shortExp, 53m, isLong: false, 1.5m),
			Leg(shortExp, 55m, isLong: true, 0.5m),
		};
		var diag = RiskDiagnosticBuilder.Build(legs, spot: 50m, asOf, ivResolver: _ => 0.40m, trend: null);
		Assert.Equal("diagonal_vertical", diag.StructureLabel);
		Assert.Equal("neutral", diag.DirectionalBias);
	}
}
