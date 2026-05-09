using WebullAnalytics;
using WebullAnalytics.Analyze;
using WebullAnalytics.Pricing;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public class CombinedBreakEvenAnalyzerTests
{
	private const string Ticker = "GME";

	private static AnalysisOptions OptionsWith(decimal underlying, Dictionary<string, decimal>? ivOverrides = null)
		=> new()
		{
			UnderlyingPrices = new Dictionary<string, decimal> { [Ticker] = underlying },
			IvOverrides = ivOverrides,
			Theoretical = true,
		};

	private static PositionRow BuildLegRow(string occ, Side side, int qty, decimal price, DateTime expiry, decimal strike, string callPut, bool firstInUnit)
	{
		return new PositionRow(
			Instrument: occ,
			Asset: Asset.Option,
			OptionKind: callPut == "C" ? "Call" : "Put",
			Side: side,
			Qty: qty,
			AvgPrice: price,
			Expiry: expiry,
			IsStrategyLeg: !firstInUnit,
			MatchKey: MatchKeys.Option(occ)
		);
	}

	private static List<PositionRow> BuildUnit(params (string occ, Side side, int qty, decimal price, DateTime expiry, decimal strike, string callPut)[] legs)
	{
		var rows = new List<PositionRow>();
		for (int i = 0; i < legs.Length; i++)
		{
			var (occ, side, qty, price, expiry, strike, callPut) = legs[i];
			rows.Add(BuildLegRow(occ, side, qty, price, expiry, strike, callPut, firstInUnit: i == 0));
		}
		return rows;
	}

	[Fact]
	public void Combined_BalancedDoubleCalendar_ReportsPerStructureNetDebit()
	{
		// Two units, merge into a balanced 4-leg double calendar at 300 contracts each.
		// User's exact case: -SP24 May15 - SC25 May15 + LP24 May29 + LC25 May29.
		// Expected per-structure net = -0.425 - 0.328 + 0.915 + 0.758 = 0.920.
		var nearExp = new DateTime(2026, 5, 15);
		var farExp = new DateTime(2026, 5, 29);

		var unit1 = BuildUnit(
			("GME260515P00024000", Side.Sell, 300, 0.425m, nearExp, 24m, "P"),
			("GME260515C00025000", Side.Sell, 300, 0.328m, nearExp, 25m, "C")
		);
		var unit2 = BuildUnit(
			("GME260529P00024000", Side.Buy, 300, 0.915m, farExp, 24m, "P"),
			("GME260529C00025000", Side.Buy, 300, 0.758m, farExp, 25m, "C")
		);
		var allRows = unit1.Concat(unit2).ToList();

		var opts = OptionsWith(underlying: 24.28m);
		var results = CombinedBreakEvenAnalyzer.Analyze(allRows, opts);

		var combined = Assert.Single(results);
		Assert.Contains("300x", combined.Details);
		Assert.Contains("$0.92 adj", combined.Details);
	}

	[Fact]
	public void Combined_BalancedIronCondor_ReportsPerStructureNetCredit()
	{
		// 4-leg iron condor, 100 each, single expiry.
		// Net credit per structure = +SP - LP + SC - LC = +0.50 - 0.20 + 0.55 - 0.25 = 0.60.
		var exp = new DateTime(2026, 6, 19);
		var unit1 = BuildUnit(
			("GME260619P00023000", Side.Buy, 100, 0.20m, exp, 23m, "P"),
			("GME260619P00024000", Side.Sell, 100, 0.50m, exp, 24m, "P")
		);
		var unit2 = BuildUnit(
			("GME260619C00026000", Side.Sell, 100, 0.55m, exp, 26m, "C"),
			("GME260619C00027000", Side.Buy, 100, 0.25m, exp, 27m, "C")
		);
		var allRows = unit1.Concat(unit2).ToList();

		var opts = OptionsWith(underlying: 25m);
		var results = CombinedBreakEvenAnalyzer.Analyze(allRows, opts);

		var combined = Assert.Single(results);
		Assert.Contains("100x", combined.Details);
		Assert.Contains("$0.60 adj", combined.Details);
	}

	[Fact]
	public void Combined_UnevenLegs_KeepsLegacyPerPairQuoting()
	{
		// Uneven case: 100 short put + 200 long put + 100 short call.
		// Quantities differ across legs, so per-pair quoting is preserved (legacy behavior):
		// pairQty = min(longQty=200, shortQty=200) = 200, weights {SP=0.5, LP=1, SC=0.5}.
		// Per-pair net = -0.5*0.40 + 1*0.30 - 0.5*0.45 = -0.125, |abs| = $0.125 adj, "200x".
		// Total dollars: 200 × 0.125 × 100 = $2,500 net debit (recovered regardless of denominator).
		var exp = new DateTime(2026, 6, 19);
		var unit1 = BuildUnit(
			("GME260619P00023000", Side.Buy, 200, 0.30m, exp, 23m, "P"),
			("GME260619P00024000", Side.Sell, 100, 0.40m, exp, 24m, "P")
		);
		var unit2 = BuildUnit(
			("GME260619C00026000", Side.Sell, 100, 0.45m, exp, 26m, "C")
		);
		var allRows = unit1.Concat(unit2).ToList();

		var opts = OptionsWith(underlying: 25m);
		var results = CombinedBreakEvenAnalyzer.Analyze(allRows, opts);

		var combined = Assert.Single(results);
		Assert.Contains("200x", combined.Details);
		Assert.Contains("$0.125 adj", combined.Details);
	}

	[Fact]
	public void Combined_AsymmetricRatio_KeepsLegacyPerPairQuoting()
	{
		// 300 short put + 300 long put + 400 short call (no long call). Legs differ → legacy quoting.
		// pairQty = min(longQty=300, shortQty=700) = 300. Weights {SP=1, LP=1, SC=4/3}.
		// Per-pair net = -1*0.40 + 1*0.30 - (4/3)*0.45 = -0.70. |abs| = $0.70 adj, "300x".
		// Total dollars: 300 × 0.70 × 100 = $21,000 net debit.
		var exp = new DateTime(2026, 6, 19);
		var unit1 = BuildUnit(
			("GME260619P00023000", Side.Buy, 300, 0.30m, exp, 23m, "P"),
			("GME260619P00024000", Side.Sell, 300, 0.40m, exp, 24m, "P")
		);
		var unit2 = BuildUnit(
			("GME260619C00026000", Side.Sell, 400, 0.45m, exp, 26m, "C")
		);
		var allRows = unit1.Concat(unit2).ToList();

		var opts = OptionsWith(underlying: 25m);
		var results = CombinedBreakEvenAnalyzer.Analyze(allRows, opts);

		var combined = Assert.Single(results);
		Assert.Contains("300x", combined.Details);
		Assert.Contains("$0.70 adj", combined.Details);
	}

	[Fact]
	public void Combined_SingleVerticalAcrossPositions_UnchangedQuoting()
	{
		// Two distinct positions on the same ticker that merge into a single short vertical: 100 each.
		// Old (pairQty = min(100,100) = 100) and new (gcd = 100) match.
		// Net credit per share = 0.50 - 0.20 = 0.30. "100x @ $0.30 adj".
		var exp = new DateTime(2026, 6, 19);
		var unit1 = BuildUnit(
			("GME260619P00024000", Side.Sell, 100, 0.50m, exp, 24m, "P")
		);
		var unit2 = BuildUnit(
			("GME260619P00023000", Side.Buy, 100, 0.20m, exp, 23m, "P")
		);
		var allRows = unit1.Concat(unit2).ToList();

		var opts = OptionsWith(underlying: 25m);
		var results = CombinedBreakEvenAnalyzer.Analyze(allRows, opts);

		var combined = Assert.Single(results);
		Assert.Contains("100x", combined.Details);
		Assert.Contains("$0.30 adj", combined.Details);
	}
}
