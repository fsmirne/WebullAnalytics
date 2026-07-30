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
}
