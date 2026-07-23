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
	public void LostLegRowsAreCaughtByHeaderStrikeArity()
	{
		// Real failure: a 4-leg iron butterfly whose Leg 3 lost its red "Call" and whose Leg 4 was fully
		// garbled — only 2 legs parse. The header's 4-strike field is the tell; emitting a 2-leg order at
		// the full-structure limit would be catastrophic.
		var rows = new[]
		{
			"iron Butterfly GME 20.5/21.5/21.5/23 31 Jul 26(W) Sell 499 Limit $0.64 Day",
			"Leg 1 GME 20.5 31 Jul 26(W) Put Buy 499",
			"Leg 2 GME 21.5 31 Jul 26(W) Put Sell 499",
			"Leg 3 GME 21.5 31 Jul 26(W) Sell 499",
			"ig ome 3152600 ay 499",
		};
		var p = WebullTicketParser.Parse(rows);
		Assert.Equal(2, p.Legs.Count);
		Assert.Contains(p.Problems, x => x.Contains("OCR lost leg rows"));
	}

	[Fact]
	public void FourLegTicketWithAllRowsParsesClean()
	{
		var rows = new[]
		{
			"iron Butterfly GME 20.5/21.5/21.5/23 31 Jul 26(W) Sell 499 Limit $0.64 Day",
			"Leg 1 GME 20.5 31 Jul 26(W) Put Buy 499",
			"Leg 2 GME 21.5 31 Jul 26(W) Put Sell 499",
			"Leg 3 GME 21.5 31 Jul 26(W) Call Sell 499",
			"Leg 4 GME 23 31 Jul 26(W) Call Buy 499",
		};
		var p = WebullTicketParser.Parse(rows);
		Assert.Empty(p.Problems);
		Assert.Equal(4, p.Legs.Count);
		Assert.Equal(0.64m, p.NetLimit);
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
	public void MergeUnionsLegsAcrossPassesAndBreaksQtyTiesWithHeaderQty()
	{
		// Pass A (psm 4): sees legs 1-2 cleanly. Pass B (psm 11): sees legs 3-4 but fragments leg 1's qty
		// ("499" -> "19"). Union recovers all four; the 499-vs-19 tie is broken by the voted header qty.
		var passA = WebullTicketParser.Parse(new[]
		{
			"iron Butterfly GME 20.5/21.5/21.5/23 31 Jul 26(W) Sell 499 Limit $0.64 Day",
			"Leg 1 GME 20.5 31 Jul 26(W) Put Buy 499",
			"Leg 2 GME 21.5 31 Jul 26(W) Put Sell 499",
		});
		var passB = WebullTicketParser.Parse(new[]
		{
			"iron Butterfly GME 20.5/21.5/21.5/23 31 Jul 26(W) Sell 499 Limit $0.64 Day",
			"Leg 1 GME 20.5 31 Jul 26(W) Put Buy 19",
			"Leg 3 GME 21.5 31 Jul 26(W) Call Sell 499",
			"Leg 4 GME 23 31 Jul 26(W) Call Buy 499",
		});
		var m = WebullTicketParser.Merge([passA, passB]);
		Assert.Empty(m.Problems);
		Assert.Equal(4, m.Legs.Count);
		Assert.All(m.Legs, l => Assert.Equal(499, l.Qty));
		Assert.Equal(0.64m, m.NetLimit);
	}

	[Fact]
	public void MergeFlagsQtyTieTheHeaderCannotBreak()
	{
		var passA = WebullTicketParser.Parse(new[] { "Diagonal GME 21/21.5 24 Jul 26(W)/07 Aug 26(W) Call Buy 499 Limit $0.63 Day", "Leg 1 GME 21 07 Aug 26(W) Call Buy 400" });
		var passB = WebullTicketParser.Parse(new[] { "Diagonal GME 21/21.5 24 Jul 26(W)/07 Aug 26(W) Call Buy 499 Limit $0.63 Day", "Leg 1 GME 21 07 Aug 26(W) Call Buy 410" });
		var m = WebullTicketParser.Merge([passA, passB]);
		Assert.Contains(m.Problems, x => x.Contains("passes disagree on qty"));
	}

	[Fact]
	public void ReconstructsSingleMissingLegFromHeaderAndPartialRow()
	{
		// Real case: Leg 4's strike "22" OCR'd as "2" and its side mangled, so the full parse never sees it.
		// Header strike field + the partial row + shared expiry + header qty rebuild it — with a warning.
		var pass = WebullTicketParser.Parse(new[]
		{
			"iron Butterfly GME 20/21.5/21.5/22 07 Aug 26(W) Sell 250 Limit $0.56 Day",
			"Leg 1 GME 20 07 Aug 26(W) Put Buy 250",
			"Leg 2 GME 21.5 07 Aug 26(W) Put Sell 250",
			"Leg 3 GME 21.5 07 Aug 26(W) Call Sell 250",
			"Leg 4 GME 2 07 Aug 26(W) Call “Buy 250",
		});
		var m = WebullTicketParser.Merge([pass]);
		Assert.Empty(m.Problems);
		Assert.Single(m.Warnings);
		Assert.Equal(4, m.Legs.Count);
		var rebuilt = m.Legs.Single(l => l.OccSymbol == "GME260807C00022000");
		Assert.Equal("buy", rebuilt.Action);
		Assert.Equal(250, rebuilt.Qty);
	}

	[Fact]
	public void ReconstructionDeducesSideFromDefinedRiskStructureWhenUnreadable()
	{
		// Real case: OCR renders "Buy" as "By" — unusable (never fuzzy-match side). But a single-expiry
		// 2P/2C 4-strike ticket is an iron structure where each type is a vertical: the recovered call is a
		// SELL, so the missing 22C wing is forced to BUY.
		var pass = WebullTicketParser.Parse(new[]
		{
			"iron Butterfly GME 20/21.5/21.5/22 07 Aug 26(W) Sell 250 Limit $0.56 Day",
			"Leg 1 GME 20 07 Aug 26(W) Put Buy 250",
			"Leg 2 GME 21.5 07 Aug 26(W) Put Sell 250",
			"Leg 3 GME 21.5 07 Aug 26(W) Call Sell 250",
			"Leg 4 GME 22 Call “By 250",
		});
		var m = WebullTicketParser.Merge([pass]);
		Assert.Empty(m.Problems);
		Assert.Contains(m.Warnings, w => w.Contains("DEDUCED"));
		var rebuilt = m.Legs.Single(l => l.OccSymbol == "GME260807C00022000");
		Assert.Equal("buy", rebuilt.Action);
	}

	[Fact]
	public void ReconstructionAbortsWhenTwoLegsAreMissing()
	{
		var pass = WebullTicketParser.Parse(new[]
		{
			"iron Butterfly GME 20/21.5/21.5/22 07 Aug 26(W) Sell 250 Limit $0.56 Day",
			"Leg 1 GME 20 07 Aug 26(W) Put Buy 250",
			"Leg 2 GME 21.5 07 Aug 26(W) Put Sell 250",
		});
		var m = WebullTicketParser.Merge([pass]);
		Assert.Empty(m.Warnings);
		Assert.Contains(m.Problems, x => x.Contains("header lists 4 strikes"));
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
