using WebullAnalytics.Report;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Report;

/// <summary>The cash-record overlay must match posted rows to legs across the quirks the live ledger
/// showed on 2026-07-30: posting times lag fills by up to ~20 minutes (so only the DATE is a key),
/// recent descriptions omit the strike (older ones carry it — enforced only when present), and the
/// parent gets broker cash only when every leg matched.</summary>
public class BrokerCashOverlayTests
{
	private static readonly DateTime FillTime = new(2026, 7, 14, 10, 45, 56);
	private static readonly DateTime BackExpiry = new(2026, 8, 21);
	private static readonly DateTime FrontExpiry = new(2026, 7, 17);

	private static List<Trade> CloseCombo() =>
	[
		new(1, FillTime, "GME 21 Aug 2026", "strategy:Diagonal:GME:2026-08-21:C21,C22.5", Asset.OptionStrategy, "Diagonal", Side.Sell, 150, 1.58m, Trade.OptionMultiplier, BackExpiry),
		new(2, FillTime, Formatters.FormatOptionDisplay("GME", BackExpiry, 21m), MatchKeys.Option(MatchKeys.OccSymbol("GME", BackExpiry, 21m, "C")), Asset.Option, "Call", Side.Sell, 150, 1.84m, Trade.OptionMultiplier, BackExpiry, 1, Fee: 7.86m),
		new(3, FillTime, Formatters.FormatOptionDisplay("GME", FrontExpiry, 22.5m), MatchKeys.Option(MatchKeys.OccSymbol("GME", FrontExpiry, 22.5m, "C")), Asset.Option, "Call", Side.Buy, 150, 0.26m, Trade.OptionMultiplier, FrontExpiry, 1, Fee: 6.80m),
	];

	private static string WriteCashRecord(params string[] lines)
	{
		var dir = Directory.CreateTempSubdirectory("wa-cashrecord-test").FullName;
		File.WriteAllLines(Path.Combine(dir, "cashrecord.jsonl"), lines);
		return dir;
	}

	[Fact]
	public void Apply_MatchesLaggedStrikelessRows_AndSumsParent()
	{
		// Posted 19 minutes after the fill; sell row strike-less (recent format), buy row with strike (older format).
		var dir = WriteCashRecord(
			"""{"name":"Trade","description":"Sold GME 20260821C","amount":"27591.14","totalAmount":"1.00","occurredTime":"07/14/2026 11:04:39 EDT"}""",
			"""{"name":"Trade","description":"Bought GME  20260717C  22.500","amount":"-3905.80","totalAmount":"1.00","occurredTime":"07/14/2026 11:04:39 EDT"}""",
			"""{"name":"Cash Transfer","description":"ACH Deposit","amount":"42347.35","totalAmount":"1.00","occurredTime":"07/13/2026 06:09:08 EDT"}""");

		var trades = CloseCombo();
		BrokerCashOverlay.Apply(trades, dir);

		Assert.Equal(27591.14m, trades[1].BrokerCash);
		Assert.Equal(-3905.80m, trades[2].BrokerCash);
		Assert.Equal(27591.14m - 3905.80m, trades[0].BrokerCash);
	}

	[Fact]
	public void Apply_PartiallyMatchedCombo_LeavesParentComputed()
	{
		var dir = WriteCashRecord(
			"""{"name":"Trade","description":"Sold GME 20260821C","amount":"27591.14","totalAmount":"1.00","occurredTime":"07/14/2026 11:04:39 EDT"}""");

		var trades = CloseCombo();
		BrokerCashOverlay.Apply(trades, dir);

		Assert.Equal(27591.14m, trades[1].BrokerCash);
		Assert.Null(trades[2].BrokerCash);
		Assert.Null(trades[0].BrokerCash);
	}

	[Fact]
	public void Apply_StrikeMismatch_DoesNotMatch()
	{
		var dir = WriteCashRecord(
			"""{"name":"Trade","description":"Bought GME 20260717C 25.000","amount":"-3905.80","totalAmount":"1.00","occurredTime":"07/14/2026 11:04:39 EDT"}""");

		var trades = CloseCombo();
		BrokerCashOverlay.Apply(trades, dir);

		Assert.Null(trades[2].BrokerCash);
	}

	[Fact]
	public void Apply_SplitFills_SumsPartialFillsIntoLegAndParent()
	{
		// A 9-contract order split into 6 and 3 contracts:
		// Sell leg: 9 contracts @ 3.56, fee 0.48 -> computed 3203.52. Cash record: 2135.68 (6x) + 1067.83 (3x) = 3203.51.
		// Buy leg: 9 contracts @ 8.01, fee 0.41 -> computed -7209.41. Cash record: -4806.27 (6x) + -2403.14 (3x) = -7209.41.
		var fillTime = new DateTime(2026, 10, 2, 10, 9, 39);
		var shortExpiry = new DateTime(2026, 10, 8);
		var longExpiry = new DateTime(2026, 10, 23);
		const string parentKey = "strategy:Diagonal:SPY:2026-10-23:P770,P771";

		var trades = new List<Trade>
		{
			new(1, fillTime, "SPY 23 Oct 2026", parentKey, Asset.OptionStrategy, "Diagonal", Side.Buy, 9, 4.45m, Trade.OptionMultiplier, longExpiry),
			new(2, fillTime, Formatters.FormatOptionDisplay("SPY", longExpiry, 771m), MatchKeys.Option(MatchKeys.OccSymbol("SPY", longExpiry, 771m, "P")), Asset.Option, "Put", Side.Buy, 9, 8.01m, Trade.OptionMultiplier, longExpiry, 1, Fee: 0.41m),
			new(3, fillTime, Formatters.FormatOptionDisplay("SPY", shortExpiry, 770m), MatchKeys.Option(MatchKeys.OccSymbol("SPY", shortExpiry, 770m, "P")), Asset.Option, "Put", Side.Sell, 9, 3.56m, Trade.OptionMultiplier, shortExpiry, 1, Fee: 0.48m),
		};

		var dir = WriteCashRecord(
			"""{"name":"Trade","description":"Sold SPY 20261008P","amount":"2135.68","totalAmount":"1.00","occurredTime":"10/02/2026 10:09:39 EDT"}""",
			"""{"name":"Trade","description":"Sold SPY 20261008P","amount":"1067.83","totalAmount":"1.00","occurredTime":"10/02/2026 10:09:39 EDT"}""",
			"""{"name":"Trade","description":"Bought SPY 20261023P","amount":"-4806.27","totalAmount":"1.00","occurredTime":"10/02/2026 10:09:39 EDT"}""",
			"""{"name":"Trade","description":"Bought SPY 20261023P","amount":"-2403.14","totalAmount":"1.00","occurredTime":"10/02/2026 10:09:39 EDT"}""");

		BrokerCashOverlay.Apply(trades, dir);

		Assert.Equal(-7209.41m, trades[1].BrokerCash);
		Assert.Equal(3203.51m, trades[2].BrokerCash);
		Assert.Equal(-7209.41m + 3203.51m, trades[0].BrokerCash);
	}

	[Fact]
	public void Apply_PartialFillWithoutRemainder_DoesNotMatch()
	{
		// Only the 6-contract portion (-4806.27) is present for a 9-contract order (-7209.41 computed).
		// Because the diff exceeds tolerance ($18.00), it must not match (keeps computed fallback).
		var fillTime = new DateTime(2026, 10, 2, 10, 9, 39);
		var longExpiry = new DateTime(2026, 10, 23);
		const string parentKey = "strategy:Diagonal:SPY:2026-10-23:P770,P771";

		var trades = new List<Trade>
		{
			new(1, fillTime, "SPY 23 Oct 2026", parentKey, Asset.OptionStrategy, "Diagonal", Side.Buy, 9, 4.45m, Trade.OptionMultiplier, longExpiry),
			new(2, fillTime, Formatters.FormatOptionDisplay("SPY", longExpiry, 771m), MatchKeys.Option(MatchKeys.OccSymbol("SPY", longExpiry, 771m, "P")), Asset.Option, "Put", Side.Buy, 9, 8.01m, Trade.OptionMultiplier, longExpiry, 1, Fee: 0.41m),
		};

		var dir = WriteCashRecord(
			"""{"name":"Trade","description":"Bought SPY 20261023P","amount":"-4806.27","totalAmount":"1.00","occurredTime":"10/02/2026 10:09:39 EDT"}""");

		BrokerCashOverlay.Apply(trades, dir);

		Assert.Null(trades[1].BrokerCash);
		Assert.Null(trades[0].BrokerCash);
	}
}
