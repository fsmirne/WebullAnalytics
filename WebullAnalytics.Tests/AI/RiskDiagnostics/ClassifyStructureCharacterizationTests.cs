using WebullAnalytics;
using WebullAnalytics.AI.RiskDiagnostics;
using Xunit;

namespace WebullAnalytics.Tests.AI.RiskDiagnostics;

/// <summary>Characterization tests: they lock the CURRENT (label, bias) output of
/// RiskDiagnosticBuilder.ClassifyStructure across every branch, so the classifier-consolidation
/// refactor can be proven behavior-preserving. These assert what the code does today (including any
/// latent bias quirks) — they are a safety net, not a statement that every label is "correct".</summary>
public class ClassifyStructureCharacterizationTests
{
	private static readonly DateTime AsOf = new(2026, 4, 20);
	private static readonly DateTime Near = new(2026, 4, 24);
	private static readonly DateTime Far = new(2026, 5, 15);

	private static DiagnosticLeg Opt(string cp, DateTime exp, decimal strike, bool isLong, decimal price) =>
		new(Symbol: $"SPY{exp:yyMMdd}{cp}{strike}", Parsed: new OptionParsed("SPY", exp, cp, strike), IsLong: isLong, Qty: 100, PricePerShare: price, CostBasisPerShare: null);

	private static (string Label, string Bias) Classify(params DiagnosticLeg[] legs)
	{
		var d = RiskDiagnosticBuilder.Build(legs, spot: 50m, AsOf, ivResolver: _ => 0.40m, trend: null);
		return (d.StructureLabel, d.DirectionalBias);
	}

	[Fact] public void SingleLongCall() => Assert.Equal(("single_long", "bullish"), Classify(Opt("C", Near, 50m, true, 1m)));
	[Fact] public void SingleLongPut() => Assert.Equal(("single_long", "bearish"), Classify(Opt("P", Near, 50m, true, 1m)));
	[Fact] public void SingleShortCall() => Assert.Equal(("single_short", "bearish"), Classify(Opt("C", Near, 50m, false, 1m)));
	[Fact] public void SingleShortPut() => Assert.Equal(("single_short", "bullish"), Classify(Opt("P", Near, 50m, false, 1m)));

	// Same-expiry 1L1S: debit when long leg costs more than short leg, else credit.
	[Fact] public void CallDebitVertical() => Assert.Equal(("vertical_debit", "bullish"), Classify(Opt("C", Near, 50m, true, 2m), Opt("C", Near, 52m, false, 1m)));
	[Fact] public void CallCreditVertical() => Assert.Equal(("vertical_credit", "bullish"), Classify(Opt("C", Near, 52m, true, 1m), Opt("C", Near, 50m, false, 2m)));
	[Fact] public void PutDebitVertical() => Assert.Equal(("vertical_debit", "bullish"), Classify(Opt("P", Near, 52m, true, 2m), Opt("P", Near, 50m, false, 1m)));
	[Fact] public void PutCreditVertical() => Assert.Equal(("vertical_credit", "bullish"), Classify(Opt("P", Near, 50m, true, 1m), Opt("P", Near, 52m, false, 2m)));

	// Diff-expiry 1L1S.
	[Fact] public void SameStrikeIsCalendar() => Assert.Equal(("calendar", "neutral"), Classify(Opt("C", Far, 50m, true, 2m), Opt("C", Near, 50m, false, 1m)));
	[Fact] public void CoveredDiagonalCall() => Assert.Equal(("covered_diagonal", "bullish"), Classify(Opt("C", Far, 50m, true, 2m), Opt("C", Near, 52m, false, 1m)));
	[Fact] public void CoveredDiagonalPut() => Assert.Equal(("covered_diagonal", "bearish"), Classify(Opt("P", Far, 52m, true, 2m), Opt("P", Near, 50m, false, 1m)));
	[Fact] public void InvertedDiagonalCall() => Assert.Equal(("inverted_diagonal", "bearish"), Classify(Opt("C", Far, 52m, true, 2m), Opt("C", Near, 50m, false, 1m)));

	// Single-expiry 2L2S iron structures.
	[Fact] public void IronButterfly() => Assert.Equal(("iron_butterfly", "neutral"), Classify(
		Opt("P", Near, 48m, true, 1m), Opt("P", Near, 50m, false, 2m), Opt("C", Near, 50m, false, 2m), Opt("C", Near, 52m, true, 1m)));
	[Fact] public void IronCondor() => Assert.Equal(("iron_condor", "neutral"), Classify(
		Opt("P", Near, 46m, true, 1m), Opt("P", Near, 48m, false, 2m), Opt("C", Near, 52m, false, 2m), Opt("C", Near, 54m, true, 1m)));

	// Two-expiry 2L2S two-sided structures.
	[Fact] public void DoubleCalendar() => Assert.Equal(("double_calendar", "neutral"), Classify(
		Opt("P", Far, 48m, true, 2m), Opt("P", Near, 48m, false, 1m), Opt("C", Far, 52m, true, 2m), Opt("C", Near, 52m, false, 1m)));
	[Fact] public void DoubleDiagonal() => Assert.Equal(("double_diagonal", "neutral"), Classify(
		Opt("P", Far, 47m, true, 2m), Opt("P", Near, 48m, false, 1m), Opt("C", Far, 53m, true, 2m), Opt("C", Near, 52m, false, 1m)));

	// Single-sided 2L2S two-expiry verticals.
	[Fact] public void CalendarVertical() => Assert.Equal(("calendar_vertical", "neutral"), Classify(
		Opt("C", Far, 50m, true, 2m), Opt("C", Far, 52m, false, 1m), Opt("C", Near, 50m, false, 1.5m), Opt("C", Near, 52m, true, 0.5m)));
	[Fact] public void DiagonalVertical() => Assert.Equal(("diagonal_vertical", "neutral"), Classify(
		Opt("C", Far, 50m, true, 2m), Opt("C", Far, 52m, false, 1m), Opt("C", Near, 53m, false, 1.5m), Opt("C", Near, 55m, true, 0.5m)));

	// Malformed / degenerate sets fall through to unknown.
	[Fact] public void MalformedIronFallsToUnknown() => Assert.Equal(("unknown", "neutral"), Classify(
		// 2L2S single-expiry but not a valid IB/IC geometry (longs inside the shorts).
		Opt("P", Near, 50m, true, 1m), Opt("P", Near, 48m, false, 2m), Opt("C", Near, 50m, false, 2m), Opt("C", Near, 48m, true, 1m)));
}
