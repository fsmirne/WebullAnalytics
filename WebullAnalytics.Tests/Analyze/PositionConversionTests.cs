using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// `analyze position` on a single long call/put sweeps the listed shorts that convert it into a vertical, calendar or diagonal (a leg-in: the long is
// kept, one short is sold against it), instead of offering only the same-strike next-weekly calendar.
public class PositionConversionTests
{
	private static readonly DateTime AsOf = new(2026, 11, 20);
	private static readonly DateTime LongExp = new(2026, 12, 18);
	private static readonly DateTime[] Expiries = [new(2026, 11, 23), new(2026, 11, 24), new(2026, 11, 25), new(2026, 11, 27), new(2026, 11, 30), new(2026, 12, 1), new(2026, 12, 2), new(2026, 12, 4), new(2026, 12, 11), LongExp, new(2027, 1, 15)];
	private const decimal Spot = 700m;

	private static OptionParsed Call(DateTime exp, decimal strike) => new("SPY", exp, "C", strike);

	private static string Sym(DateTime exp, decimal strike) => MatchKeys.OccSymbol("SPY", exp, strike, "C");

	/// <summary>Calls at 680 and 690–710 on every expiry, two-sided and liquid: bid = intrinsic + $1, ask $0.10 wider.</summary>
	private static Dictionary<string, OptionContractQuote> Chain()
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		var strikes = new[] { 680m }.Concat(Enumerable.Range(690, 21).Select(k => (decimal)k));
		foreach (var exp in Expiries)
			foreach (var k in strikes)
			{
				var bid = Math.Max(0m, Spot - k) + 1m;
				quotes[Sym(exp, k)] = new OptionContractQuote(Sym(exp, k), null, bid, bid + 0.10m, null, null, 50, 100, 0.20m);
			}
		return quotes;
	}

	private static AnalyzePositionCommand.PositionSnapshot Long680(int qty = 2) => new(Sym(LongExp, 680m), LegAction.Buy, qty, 15m, Call(LongExp, 680m));

	private static List<AnalyzePositionCommand.Scenario> Conversions(bool shortlist) =>
		AnalyzePositionCommand.GenerateScenarios([Long680()], AnalyzePositionCommand.StructureKind.SingleLong, new AnalyzePositionSettings { IvDefault = 20m, StrikeStep = 1m }, Spot, AsOf, Chain(), shortlistSwept: shortlist)
			.Where(s => s.Name.StartsWith("Convert to ", StringComparison.Ordinal)).ToList();

	[Fact]
	public void TheSweepOffersVerticalsCalendarsAndDiagonalsThatKeepTheLong()
	{
		var conversions = Conversions(shortlist: false);

		Assert.Contains(conversions, s => s.Name.StartsWith("Convert to vertical (sell 12-18", StringComparison.Ordinal));
		Assert.Contains(conversions, s => s.Name.StartsWith("Convert to covered diagonal (sell 11-23", StringComparison.Ordinal));
		// The long's 680 strike is 20 strikes from spot: its calendars come from the long-strike window, not the ±5 around spot.
		Assert.Contains(conversions, s => s.Name == "Convert to calendar (sell 11-23 $680.00)");
		Assert.All(conversions, s =>
		{
			Assert.True(s.Swept && s.IsLegIn && !s.IsRoll);
			var held = Assert.Single(s.Book!);
			Assert.Equal([(LegAction.Buy, Sym(LongExp, 680m)), (LegAction.Sell, held.Legs[1].Symbol)], held.Legs.Select(l => (l.Action, l.Symbol)));
			Assert.True(held.Legs[1].Parsed.ExpiryDate <= LongExp);   // never a short outliving the long
			Assert.Equal(held.Label, s.Name["Convert to ".Length..s.Name.IndexOf(" (", StringComparison.Ordinal)]);
		});
		Assert.DoesNotContain(conversions, s => s.Name.Contains("01-15", StringComparison.Ordinal));
	}

	[Fact]
	public void AVerticalStatesTheFloorItLocks()
	{
		// Sell the 12-18 700C (mid $1.05) against the 680C bought at $15: the worst case at expiry is both worthless, $1.05 − $15.
		var vertical = Assert.Single(Conversions(shortlist: false), s => s.Name == "Convert to vertical (sell 12-18 $700.00)");
		Assert.Contains("locks a floor of $-13.95/share", vertical.Rationale);
		Assert.Equal(105m, vertical.CashImpactPerContract);
	}

	[Fact]
	public void WithoutTheScorerTheSweepIsShortlisted()
	{
		var all = Conversions(shortlist: false);
		var shortlisted = Conversions(shortlist: true);
		Assert.True(all.Count > 5);
		Assert.InRange(shortlisted.Count, 1, 5);
	}

	[Fact]
	public void ALegInReproducesAsTheStructureItForms()
	{
		var conversion = Conversions(shortlist: false).First(s => s.Name == "Convert to vertical (sell 12-18 $700.00)");
		var (trades, analyze) = AnalyzePositionCommand.BuildReproductionCommands(conversion, new AnalyzePositionSettings(), [Long680()]);

		Assert.Equal($"wa trade place \"sell:{Sym(LongExp, 700m)}:2\" --limit 1.05", Assert.Single(trades!));
		Assert.Equal($"wa analyze trade \"buy:{Sym(LongExp, 680m)}:2@15,sell:{Sym(LongExp, 700m)}:2@1.05\"", analyze);
	}

	[Theory]
	[InlineData("C", 700, "12-18", "vertical")]
	[InlineData("C", 660, "12-18", "inverted vertical")]
	[InlineData("C", 680, "11-27", "calendar")]
	[InlineData("C", 700, "11-27", "covered diagonal")]
	[InlineData("C", 660, "11-27", "inverted diagonal")]
	[InlineData("P", 660, "11-27", "covered diagonal")]
	[InlineData("P", 700, "12-18", "inverted vertical")]
	public void PairShapeLabelNamesTheStructure(string callPut, int shortStrike, string shortExp, string expected)
	{
		var exp = shortExp == "12-18" ? LongExp : new DateTime(2026, 11, 27);
		Assert.Equal(expected, AnalyzePositionCommand.PairShapeLabel(new OptionParsed("SPY", exp, callPut, shortStrike), new OptionParsed("SPY", LongExp, callPut, 680m)));
	}

	[Theory]
	[InlineData("C", 700, 1.05, -13.95)]   // debit vertical: worst case both expire worthless
	[InlineData("C", 670, 16.00, -9.00)]   // inverted (credit) vertical: also loses the 10-point width
	[InlineData("P", 660, 2.00, -13.00)]
	public void VerticalFloorIsCreditLessBasisLessAnInvertedWidth(string callPut, int shortStrike, decimal credit, decimal expected) =>
		Assert.Equal(expected, AnalyzePositionCommand.VerticalFloorPerShare(new OptionParsed("SPY", LongExp, callPut, shortStrike), new OptionParsed("SPY", LongExp, callPut, 680m), credit, 15m));
}
