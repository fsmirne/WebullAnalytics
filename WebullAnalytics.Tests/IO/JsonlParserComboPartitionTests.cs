using WebullAnalytics;
using WebullAnalytics.IO;
using Xunit;

namespace WebullAnalytics.Tests.IO;

// Guards the same-second combo collision fix: Webull's web export has no combo id, only transactTime
// (second resolution), so two combo orders filling in the same second used to merge into one phantom
// strategy (live 2026-06-11: a put-diagonal close and a call-diagonal close from two DIFFERENT
// DoubleDiagonals reported as one 4-leg "DoubleDiagonal"). Webull rejects 4-leg multi-expiry tickets,
// so such a group is provably ≥2 combos and the parser must partition it.
public class JsonlParserComboPartitionTests : IDisposable
{
	private readonly string _path = Path.Combine(Path.GetTempPath(), $"wa-jsonl-test-{Guid.NewGuid():N}.jsonl");

	public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

	private void WriteOrders(params (string Symbol, string Sub, string Action, long TransactTime)[] rows)
	{
		var list = rows.Select(r => $"{{\"orderId\":\"{Guid.NewGuid():N}\",\"symbol\":\"{r.Symbol}\",\"subSymbol\":\"{r.Sub}\",\"filledTime\":\"06/11/2026 11:28:17 EDT\",\"transactTime\":{r.TransactTime},\"currency\":\"USD\",\"action\":\"{r.Action}\",\"quantity\":\"1.00000\",\"filledPrice\":\"5.00\",\"fee\":\"0.10\",\"commission\":\"0.00\",\"tickerType\":\"OPTION\",\"ticker\":{{}}}}");
		File.WriteAllText(_path, $"{{\"orderList\":[{string.Join(",", list)}]}}\n");
	}

	private static List<Trade> Parents(List<Trade> trades) => trades.Where(t => t.Asset == Asset.OptionStrategy).ToList();

	[Fact]
	public void TwoDiagonalCloses_SameSecond_MixedSides_SplitByCallPut()
	{
		// The live 2026-06-11 collision verbatim: DD#1's put close + DD#2's call close, one second.
		WriteOrders(
			("SPY $722.00", "30 Jun 26 Put 100", "SELL", 1781191697000),
			("SPY $726.00", "16 Jun 26 Put 100", "BUY", 1781191697000),
			("SPY $733.00", "30 Jun 26 Call 100", "SELL", 1781191697000),
			("SPY $729.00", "16 Jun 26 Call 100", "BUY", 1781191697000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		var parents = Parents(trades);
		Assert.Equal(2, parents.Count);
		Assert.All(parents, p => Assert.Equal("Diagonal", p.OptionKind));
	}

	[Fact]
	public void IronCondor_SingleExpiry_StaysOneStrategy()
	{
		// 4 legs, one expiry — a legal single Webull ticket; must NOT be split.
		WriteOrders(
			("SPY $700.00", "16 Jun 26 Put 100", "BUY", 1781191697000),
			("SPY $705.00", "16 Jun 26 Put 100", "SELL", 1781191697000),
			("SPY $740.00", "16 Jun 26 Call 100", "SELL", 1781191697000),
			("SPY $745.00", "16 Jun 26 Call 100", "BUY", 1781191697000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		Assert.Single(Parents(trades));
	}

	[Fact]
	public void DiagonalVertical_SameSide_SplitByExpiry()
	{
		// All calls, two expiries, buy+sell per expiry: the near+far vertical placement shape.
		WriteOrders(
			("SPY $730.00", "16 Jun 26 Call 100", "SELL", 1781191697000),
			("SPY $735.00", "16 Jun 26 Call 100", "BUY", 1781191697000),
			("SPY $732.00", "30 Jun 26 Call 100", "BUY", 1781191697000),
			("SPY $737.00", "30 Jun 26 Call 100", "SELL", 1781191697000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		var parents = Parents(trades);
		Assert.Equal(2, parents.Count);
		Assert.All(parents, p => Assert.Equal("Vertical", p.OptionKind));
	}

	[Fact]
	public void TwoSameSideDiagonals_SameSecond_PairedByStrikeRank()
	{
		// Two put-diagonal closes in one second: all sells share one expiry, all buys the other, so
		// by-expiry grouping would pair same-action legs. Strike-rank pairing recovers the combos.
		WriteOrders(
			("SPY $722.00", "30 Jun 26 Put 100", "SELL", 1781191697000),
			("SPY $726.00", "16 Jun 26 Put 100", "BUY", 1781191697000),
			("SPY $721.00", "30 Jun 26 Put 100", "SELL", 1781191697000),
			("SPY $725.00", "16 Jun 26 Put 100", "BUY", 1781191697000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		var parents = Parents(trades);
		Assert.Equal(2, parents.Count);
		var legsByParent = parents.Select(p => trades.Where(t => t.ParentStrategySeq == p.Seq).Select(t => t.Instrument).OrderBy(x => x).ToList()).ToList();
		Assert.Contains(legsByParent, legs => legs.Any(l => l.Contains("721")) && legs.Any(l => l.Contains("725")));
		Assert.Contains(legsByParent, legs => legs.Any(l => l.Contains("722")) && legs.Any(l => l.Contains("726")));
	}

	[Fact]
	public void VerticalRoll_TwoSameStrikeRollTickets_SplitByStrike()
	{
		// Live 2026-03-20: a 5-lot SPXW put vertical rolled 20Mar→23Mar as two per-leg roll combos
		// (same strike, opposite expiries) that filled in one second. Must pair by STRIKE (two
		// calendar-shaped rolls), not by expiry (two phantom verticals).
		WriteOrders(
			("SPXW $6525.00", "23 Mar 26 Put 100", "SELL", 1774032368000),
			("SPXW $6525.00", "20 Mar 26 Put 100", "BUY", 1774032368000),
			("SPXW $6520.00", "20 Mar 26 Put 100", "SELL", 1774032368000),
			("SPXW $6520.00", "23 Mar 26 Put 100", "BUY", 1774032368000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		var parents = Parents(trades);
		Assert.Equal(2, parents.Count);
		foreach (var p in parents)
		{
			var legs = trades.Where(t => t.ParentStrategySeq == p.Seq).ToList();
			Assert.Equal(2, legs.Count);
			Assert.Single(legs.Select(l => MatchKeys.ParseOption(l.MatchKey)?.parsed.Strike).Distinct()); // same strike per pair
			Assert.Equal(2, legs.Select(l => l.Expiry).Distinct().Count());                               // spanning both expiries
		}
	}

	[Fact]
	public void TwoLegCalendar_CrossExpiry_NotSplit()
	{
		WriteOrders(
			("SPY $740.00", "18 Jun 26 Put 100", "SELL", 1781191697000),
			("SPY $740.00", "02 Jul 26 Put 100", "BUY", 1781191697000));

		var (trades, _) = JsonlParser.ParseOrdersJsonl(_path);
		Assert.Single(Parents(trades));
	}
}
