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
	public void OnePoisonedHeaderReadingDoesNotFailACleanTicket()
	{
		// Real failure: one of the eight OCR passes read the header strike cell with a stray space inside the
		// second strike ("100/1 30"), which the field regex truncates to "100/1". Header fields used to come
		// from whichever pass sorted first, so that single reading failed the cross-check on a ticket the other
		// passes read correctly — and the outcome flipped with pass ORDER. The readings vote instead.
		var poisoned = WebullTicketParser.Parse(
		[
			"Vertical SPY 100/1 30 16 Oct 26 Call Buy 1 Limit $2.50 Day",
			"Leg 1 SPY 100 16 Oct 26 Call Buy 1",
			"Leg 2 SPY 130 16 Oct 26 Call Sell 1",
		]);
		var clean = WebullTicketParser.Parse(
		[
			"Vertical SPY 100/130 16 Oct 26 Call Buy 1 Limit $2.50 Day",
			"Leg 1 SPY 100 16 Oct 26 Call Buy 1",
			"Leg 2 SPY 130 16 Oct 26 Call Sell 1",
		]);
		foreach (var passes in new[] { new[] { poisoned, clean }, [clean, poisoned] })
		{
			var m = WebullTicketParser.Merge(passes);
			Assert.Empty(m.Problems);
			Assert.Equal(2, m.Legs.Count);
			Assert.Equal(2.50m, m.NetLimit);
		}
	}

	[Fact]
	public void HeaderStrikeArityIsVotedSoOneSplitTokenCannotFakeALostLeg()
	{
		// A pass that reads the header's decimal point as a slash ("21.5" -> "21/5") inflates the strike count
		// and used to raise a phantom "OCR lost leg rows" on a complete 2-leg ticket. Majority count wins.
		var split = WebullTicketParser.Parse(
		[
			"Diagonal GME 21/5/21.5 24 Jul 26(W)/07 Aug 26(W) Call Buy 499 Limit $0.63 Day",
			"Leg 1 GME 21 07 Aug 26(W) Call Buy 499",
			"Leg 2 GME 21.5 24 Jul 26(W) Call Sell 499",
		]);
		var clean = WebullTicketParser.Parse(
		[
			"Diagonal GME 21/21.5 24 Jul 26(W)/07 Aug 26(W) Call Buy 499 Limit $0.63 Day",
			"Leg 1 GME 21 07 Aug 26(W) Call Buy 499",
			"Leg 2 GME 21.5 24 Jul 26(W) Call Sell 499",
		]);
		Assert.Empty(WebullTicketParser.Merge([split, clean, clean]).Problems);
	}

	[Fact]
	public void RealHeaderStrikeMismatchStillFailsAcrossPasses()
	{
		// The tripwire must survive the vote: no pass's reading of the header reconciles with a misread leg.
		var passes = Enumerable.Range(0, 3).Select(_ => WebullTicketParser.Parse(
		[
			"Vertical SPY 100/130 16 Oct 26 Call Buy 1 Limit $2.50 Day",
			"Leg 1 SPY 100 16 Oct 26 Call Buy 1",
			"Leg 2 SPY 180 16 Oct 26 Call Sell 1",
		])).ToList();
		Assert.Contains(WebullTicketParser.Merge(passes).Problems, x => x.Contains("header strikes"));
	}

	[Fact]
	public void NetLimitIsVotedNotTakenFromTheFirstPass()
	{
		// "$0.28" read as "$0.2 8" parses 0.20. The limit is the one field the ticket does NOT repeat, so a
		// first-found limit would put a wrong PRICE on the place line with nothing to catch it.
		var wrong = WebullTicketParser.Parse(
		[
			"Calendar GME 21.5/21.5 24 Jul Aug 26(W)  Put Buy 1 ,500 Limit $0.2 8",
			"Leg 1 GME 21.5 24 Jul 26(W) Put Sell 1 ,500",
			"Leg 2 GME 21.5 07 Aug 26(W) Put Buy 1 ,500",
		]);
		Assert.Equal(0.2m, wrong.NetLimit);   // the misread pass on its own
		var right = WebullTicketParser.Parse(NoisyRows);
		var m = WebullTicketParser.Merge([wrong, right, right]);
		Assert.Equal(0.28m, m.NetLimit);
		Assert.Empty(m.Problems);
	}

	[Fact]
	public void TiedNetLimitReadingsAreReported()
	{
		var wrong = WebullTicketParser.Parse(
		[
			"Calendar GME 21.5/21.5 24 Jul Aug 26(W)  Put Buy 1 ,500 Limit $0.2 8",
			"Leg 1 GME 21.5 24 Jul 26(W) Put Sell 1 ,500",
			"Leg 2 GME 21.5 07 Aug 26(W) Put Buy 1 ,500",
		]);
		var m = WebullTicketParser.Merge([wrong, WebullTicketParser.Parse(NoisyRows)]);
		Assert.Contains(m.Problems, x => x.Contains("disagree on the net limit"));
	}

	// The eight OCR passes of a real USO put-condor snip (1391x224), verbatim from `wa clipboard order --rows`.
	// Every failure mode this parser guards against is present at once: the header strike cell breaks its tokens
	// on spaces in all eight passes ("100/110/120/130" -> "100/1 10/1 20/1 30"), the green-channel passes lose
	// the RED "Sell" word on both short legs, the max-channel passes glue the expiry ("15Jan27") and sprinkle
	// the dropdown caret between strike and date ("110 vy 15 Jan 27"), and two passes drop the TIF.
	private static readonly string[][] UsoCondorPasses =
	[
		[
			"Condor vy USO 100/1 10/1 20/1 30 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"4 4",
			"Leg 1 USO 100 vy 15Jan27 Put Buy 295",
			"Leg 2 USO 110 15 Jan 27 Put 295",
			"Leg 3 USO 120 15 Jan 27 Put 295",
			"Leg 4 USO 130 vy 15Jan27 Put Buy 295",
		],
		[
			"—int ST wn lt tobe ~~ TH ave",
			"Condor USO 100/1 10/1 20/1 30 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"Leg 1 USO 100 15 Jan 27 Put Buy 295",
			"Leg 2 USO 110 15 Jan 27 Put 295",
			"Leg 3 USO 120 15 Jan 27 Put 295",
			"Leg 4 USO 130 15 Jan 27 Put Buy 295",
		],
		[
			"Strategy Symbol Strike =xpirayuar Type Siae Qvaruy Orcer Type amt Price",
			"Condor vy USO 100/1 10/1 20/130 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"Leg 1 USO 100 15Jan27 Put Buy 295",
			"Leg 2 USO 110 vy 15 Jan 27 Put Sell 295",
			"Leg 3 USO 120 Ad 15 Jan 27 Put Sell 295",
			"Leg 4 USO 130 15Jan27 Put Buy 295",
		],
		[
			"Sirategy Symbol Strike Exprauor ype Siae Lar ty Orcer ype imt Price",
			"Condor USO 100/1 10/1 20/130 15jJan27 Put Buy 295 Limit $1.76 Day",
			"Leg 1 USO 100 15jJan27 Put Buy 295",
			"Leg 2 USO 110 15Jan27 Put Sell 295",
			"Leg 3 USO 120 15Jan27 Put Sell 295",
			"Leg 4 USO 130 15jJan27 Put Buy 295",
		],
		[
			"Wb tes ao",
			"Condor USO 100/1 10/1 20/1 30 15 Jan 27 Put Buy 295 Limit $1.76",
			"Leg 1 USO 100 15 Jan 27 Put Buy 295",
			"Leg 2 USO 110 15 Jan 27 Put 295",
			"Leg 3 USO 120 15 Jan 27 Put 295",
			"Leg 4 USO 130 15 Jan 27 Put Buy 295",
		],
		[
			"—int ST wn lt tobe ~~ TH ave",
			"Condor USO 100/1 10/1 20/1 30 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"Leg 1 USO 100 15 Jan 27 Put Buy 295",
			"Leg 2 USO 110 15 Jan 27 Put 295",
			"Leg 3 USO 120 15 Jan 27 Put 295",
			"Leg 4 USO 130 15 Jan 27 Put Buy 295",
		],
		[
			"Strategy Symbol Strike =xpirayuar Type Siae Qvaruy Orcer Type amt Price",
			"Condor vy USO 100/1 10/1 20/130 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"4 4",
			"Leg 1 USO 100 15Jan27 Put Buy 295",
			"Leg 2 USO 110 vy 15 Jan 27 Put Sell 295",
			"Leg 3 USO 120 vy 15 Jan 27 Put Sell 295",
			"Leg 4 USO 130 15Jan27 Put Buy 295",
		],
		[
			"Sirategy Symbol Strike Exprauor ype Siae Lar ty Orcer ype imt Price",
			"oe",
			"Condor USO 100/1 10/1 20/130 15jJan27 Put Buy 295 Limit $1.76",
			"Leg 1 USO 100 15jJan27 Put Buy 295",
			"Leg 2 USO 110 15Jan27 Put Sell 295",
			"Leg 3 USO 120 15Jan27 Put Sell 295",
			"Leg 4 USO 130 15jJan27 Put Buy 295",
		],
	];

	[Fact]
	public void RealUsoCondorSnipParsesAllFourLegsClean()
	{
		var m = WebullTicketParser.Merge(UsoCondorPasses.Select(WebullTicketParser.Parse).ToList());
		Assert.Empty(m.Problems);
		Assert.Empty(m.Warnings);   // all four legs READ, none reconstructed
		Assert.Equal(1.76m, m.NetLimit);
		Assert.Equal("day", m.Tif);
		Assert.Equal(new[] { "buy", "sell", "sell", "buy" }, m.Legs.Select(l => l.Action).ToArray());
		Assert.Equal(new[] { 100m, 110m, 120m, 130m }, m.Legs.Select(l => l.Strike).ToArray());
		Assert.All(m.Legs, l => Assert.Equal(295, l.Qty));
		Assert.All(m.Legs, l => Assert.Equal("USO270115P" + ((long)(l.Strike * 1000m)).ToString("00000000"), l.OccSymbol));
	}

	[Fact]
	public void SplitHeaderStrikeTokensStopAtTheExpirationDay()
	{
		// The absorption that rejoins "100/1 10" must never swallow the expiry's day number, which is the digit
		// group the letters of the month follow.
		var p = WebullTicketParser.Parse(
		[
			"Condor USO 100/1 10/1 20/1 30 15 Jan 27 Put Buy 295 Limit $1.76 Day",
			"Leg 1 USO 100 15 Jan 27 Put Buy 295",
			"Leg 2 USO 110 15 Jan 27 Put Sell 295",
			"Leg 3 USO 120 15 Jan 27 Put Sell 295",
			"Leg 4 USO 130 15 Jan 27 Put Buy 295",
		]);
		Assert.Equal("100/110/120/130", p.HeaderStrikeField);
		Assert.Empty(p.Problems);
	}

	[Fact]
	public void GluedExpiryLegRowsParse()
	{
		// Max-channel passes are the only ones that read the red "Sell"; they also glue the date cell.
		var p = WebullTicketParser.Parse(
		[
			"Vertical USO 110/120 15 Jan 27 Put Sell 295 Limit $1.76 Day",
			"Leg 1 USO 110 vy 15Jan27 Put Sell 295",
			"Leg 2 USO 120 Ad 15 Jan 27 Put Buy 295",
		]);
		Assert.Empty(p.Problems);
		Assert.Equal(new DateTime(2027, 1, 15), Assert.Single(p.Legs, l => l.Action == "sell").Expiry);
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
