using WebullAnalytics.Clipboard;
using Xunit;

namespace WebullAnalytics.Tests.Clipboard;

public class WebullTicketParserTests
{
	// Real OCR output from a Webull calendar ticket (dark theme): digits split by stray spaces
	// ("1 ,500", "21.5/21 .5"), the header expiration mangled, and a column-title row that also
	// contains the word "Limit" and must not be mistaken for the value row.
	private static readonly string[] NoisyRows =
	[
		"Strategy Symbol Strike Expiration Type Side Quantity Order Type Limit Price",
		"Calendar GME 21.5/21 .5 24 Jul Aug 26(W)  Put Buy 1 ,500 Limit $0.28",
		"Leg 1 GME 21.5 24 Jul 26(W) Put Sell 1 ,500",
		"Leg 2 GME 21.5 07 Aug 26(W) Put Buy 1 ,500",
	];

	[Fact]
	public void ParsesNoisyCalendarTicket()
	{
		var p = WebullTicketParser.Parse(NoisyRows);
		Assert.Empty(p.Problems);
		Assert.Equal(2, p.Legs.Count);
		Assert.Equal(0.28m, p.NetLimit);
		Assert.Equal("day", p.Tif);
		var sell = p.Legs.Single(l => l.Action == "sell");
		var buy = p.Legs.Single(l => l.Action == "buy");
		Assert.Equal("GME260724P00021500", sell.OccSymbol);
		Assert.Equal("GME260807P00021500", buy.OccSymbol);
		Assert.Equal(1500, sell.Qty);
		Assert.Equal(1500, buy.Qty);
	}

	[Fact]
	public void HeaderDecimalDropIsForgiven()
	{
		// Real tesseract failure on the cramped header strike cell: "21/21.5" reads as "21/215". The legs
		// carry the true strikes; a digits-only comparison must treat this as consistent.
		var rows = new[]
		{
			"Diagonal GME 21/215 24 Jul 26(W)/07 Aug 26(W) Call Buy 499 Limit $0.63 Day",
			"Leg 1 GME 21 07 Aug 26(W) Call Buy 499",
			"Leg 2 GME 21.5 24 Jul 26(W) Call Sell 499",
		};
		var p = WebullTicketParser.Parse(rows);
		Assert.Empty(p.Problems);
		Assert.Equal(0.63m, p.NetLimit);
		Assert.Equal("GME260807C00021000", p.Legs.Single(l => l.Action == "buy").OccSymbol);
		Assert.Equal("GME260724C00021500", p.Legs.Single(l => l.Action == "sell").OccSymbol);
	}

	[Fact]
	public void HeaderStrikeMismatchIsReported()
	{
		var rows = NoisyRows.ToArray();
		rows[1] = "Calendar GME 22.5/21.5 24 Jul Aug 26(W)  Put Buy 1 ,500 Limit $0.28";
		var p = WebullTicketParser.Parse(rows);
		Assert.Contains(p.Problems, x => x.Contains("header strikes"));
	}

	[Fact]
	public void HeaderQtyMismatchIsReported()
	{
		var rows = NoisyRows.ToArray();
		rows[1] = "Calendar GME 21.5/21.5 24 Jul Aug 26(W)  Put Buy 1,000 Limit $0.28";
		var p = WebullTicketParser.Parse(rows);
		Assert.Contains(p.Problems, x => x.Contains("header qty"));
	}

	[Fact]
	public void HeaderTypeMismatchIsReported()
	{
		var rows = NoisyRows.ToArray();
		rows[1] = "Calendar GME 21.5/21.5 24 Jul Aug 26(W)  Call Buy 1 ,500 Limit $0.28";
		var p = WebullTicketParser.Parse(rows);
		Assert.Contains(p.Problems, x => x.Contains("header type"));
	}

	[Fact]
	public void MissingLimitIsReported()
	{
		var p = WebullTicketParser.Parse(NoisyRows.Where((_, i) => i != 1).ToArray());
		Assert.Contains(p.Problems, x => x.Contains("limit price"));
		Assert.Equal(2, p.Legs.Count);   // legs still parse; only the place line should be withheld
	}

	[Fact]
	public void GtcIsDetected()
	{
		var rows = NoisyRows.ToArray();
		rows[1] = "Calendar GME 21.5/21 .5 24 Jul Aug 26(W)  Put Buy 1 ,500 Limit $0.28 GTC";
		Assert.Equal("gtc", WebullTicketParser.Parse(rows).Tif);
	}

	[Fact]
	public void ClusterRowsGroupsWordsByBaselineAndOrdersByX()
	{
		var words = new List<OcrWord>
		{
			new("Sell", 300, 10, 12), new("Leg", 10, 12, 12), new("1", 60, 11, 12),   // one visual row, jittered Y
			new("Buy", 300, 40, 12), new("Leg", 10, 41, 12), new("2", 60, 39, 12),    // second row
		};
		var rows = WebullTicketParser.ClusterRows(words);
		Assert.Equal(2, rows.Count);
		Assert.Equal("Leg 1 Sell", rows[0]);
		Assert.Equal("Leg 2 Buy", rows[1]);
	}
}
