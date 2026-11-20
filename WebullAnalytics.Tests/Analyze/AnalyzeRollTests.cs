using WebullAnalytics.Analyze;
using WebullAnalytics.Trading;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

public sealed class AnalyzeRollTests
{
	private static OptionContractQuote Quote(string sym, decimal? bid, decimal? ask, decimal iv = 0.35m) =>
		new(sym, LastPrice: (bid + ask) / 2m, Bid: bid, Ask: ask, Change: null, PercentChange: null, Volume: 100, OpenInterest: 500, ImpliedVolatility: iv);

	private static Dictionary<string, OptionContractQuote> BuildChain(
		string root,
		string callPut,
		DateTime[] expiries,
		decimal[] strikes,
		decimal bid = 1.00m,
		decimal ask = 1.10m)
	{
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		foreach (var exp in expiries)
			foreach (var strike in strikes)
			{
				var sym = MatchKeys.OccSymbol(root, exp, strike, callPut);
				quotes[sym] = Quote(sym, bid, ask);
			}
		return quotes;
	}

	[Fact]
	public void SettingsValidate_AllowsEmptySpecForInteractiveMode()
	{
		var settings = new AnalyzeRollSettings();
		Assert.True(settings.Validate().Successful);
	}

	[Fact]
	public void SettingsValidate_AllowsPositionSpecWithOrWithoutPrices()
	{
		var withPrices = new AnalyzeRollSettings { Spec = "sell:GME260424C00025000:10@0.48,buy:GME260515C00025000:10@1.11", Strikes = 2, Exp = 3 };
		Assert.True(withPrices.Validate().Successful);

		var withoutPrices = new AnalyzeRollSettings { Spec = "sell:GME260424C00025000:10,buy:GME:1000", Leg = "short", Sort = "credit-day" };
		Assert.True(withoutPrices.Validate().Successful);
	}

	[Fact]
	public void SettingsValidate_RejectsInvalidInputs()
	{
		Assert.False(new AnalyzeRollSettings { Spec = "sell:GME260424C00025000:10,buy:SPY260515C00500000:10" }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Spec = "buy:GME:100" }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Strikes = -1 }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Exp = -1 }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Top = -1 }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Cash = -10m }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Sort = "bogus" }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Pricing = "bogus" }.Validate().Successful);
		Assert.True(new AnalyzeRollSettings { Pricing = "bidask" }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { Leg = "not-an-occ" }.Validate().Successful);
		Assert.False(new AnalyzeRollSettings { To = "not-an-occ" }.Validate().Successful);
	}

	[Fact]
	public void Enumerate_SweepsPlusMinusStrikesAndExpiries_ExcludesOldLeg()
	{
		var today = new DateTime(2026, 4, 1);
		var expiries = new[]
		{
			new DateTime(2026, 4, 3),
			new DateTime(2026, 4, 10),
			new DateTime(2026, 4, 17), // old leg expiry
			new DateTime(2026, 4, 24),
			new DateTime(2026, 5, 1)
		};
		var strikes = new[] { 20m, 22m, 24m, 25m, 26m, 28m, 30m };
		var quotes = BuildChain("GME", "C", expiries, strikes);
		// Add puts to verify they are excluded
		foreach (var kv in BuildChain("GME", "P", expiries, strikes))
			quotes[kv.Key] = kv.Value;

		var oldSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 17), 25m, "C");
		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSym)!;

		var (candidates, skipped) = RollCandidateEnumerator.Enumerate(
			quotes, oldParsed, oldSym, oldParsed, anchorSymbol: null, LegAction.Sell, strikesN: 1, expN: 1, today: today);

		// 3 expiries (Apr 10, 17, 24) * 3 strikes (24, 25, 26) - 1 (oldSym) = 8
		Assert.Equal(8, candidates.Count);
		Assert.Equal(0, skipped);
		Assert.DoesNotContain(candidates, c => c.Symbol == oldSym);
		Assert.All(candidates, c => Assert.Equal("C", c.Parsed.CallPut));
		Assert.All(candidates, c => Assert.InRange(c.Parsed.Strike, 24m, 26m));
		Assert.All(candidates, c => Assert.InRange(c.Parsed.ExpiryDate.Date, new DateTime(2026, 4, 10), new DateTime(2026, 4, 24)));
	}

	[Fact]
	public void Enumerate_ClampsNegativeExpAtToday()
	{
		var today = new DateTime(2026, 4, 17);
		var expiries = new[]
		{
			new DateTime(2026, 4, 10), // expired (< today)
			new DateTime(2026, 4, 17), // today (0DTE)
			new DateTime(2026, 4, 24),
			new DateTime(2026, 5, 1),
			new DateTime(2026, 5, 8)
		};
		var strikes = new[] { 24m, 25m, 26m };
		var quotes = BuildChain("GME", "C", expiries, strikes);

		var oldSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 17), 25m, "C");
		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSym)!;

		var (candidates, _) = RollCandidateEnumerator.Enumerate(
			quotes, oldParsed, oldSym, oldParsed, anchorSymbol: null, LegAction.Sell, strikesN: 1, expN: 2, today: today);

		// Valid expiries >= today within ±2 of Apr 17: Apr 17, Apr 24, May 1 (3 expiries * 3 strikes - 1 old = 8)
		Assert.Equal(8, candidates.Count);
		Assert.All(candidates, c => Assert.True(c.Parsed.ExpiryDate.Date >= today));
	}

	[Fact]
	public void Enumerate_AnchorsOnToTargetAndFiltersIlliquid()
	{
		var today = new DateTime(2026, 4, 1);
		var expiries = new[] { new DateTime(2026, 4, 10), new DateTime(2026, 4, 17), new DateTime(2026, 4, 24) };
		var strikes = new[] { 20m, 25m, 30m, 35m };
		var quotes = BuildChain("GME", "C", expiries, strikes);

		// Make one contract illiquid (bid = 0 for a short roll)
		var illiquidSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 24), 35m, "C");
		quotes[illiquidSym] = Quote(illiquidSym, bid: 0m, ask: 0.05m);

		var oldSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 10), 20m, "C");
		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSym)!;
		var toSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 24), 30m, "C");
		var toParsed = ParsingHelpers.ParseOptionSymbol(toSym)!;

		var (candidates, skipped) = RollCandidateEnumerator.Enumerate(
			quotes, oldParsed, oldSym, toParsed, anchorSymbol: toSym, LegAction.Sell, strikesN: 1, expN: 0, today: today);

		// Anchor expiry Apr 24 only (expN=0), strikes 25, 30, 35; 35 is illiquid => 2 candidates
		Assert.Equal(2, candidates.Count);
		Assert.Equal(1, skipped);
		Assert.Single(candidates, c => c.IsAnchor && c.Symbol == toSym);
	}

	[Fact]
	public void SelectRolledLeg_DefaultsToShortLegNonInteractively()
	{
		var shortSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 4, 24), 25m, "C");
		var longSym = MatchKeys.OccSymbol("GME", new DateTime(2026, 5, 15), 25m, "C");
		var legs = new List<RollPositionLeg>
		{
			new(longSym, LegAction.Buy, 10, ParsingHelpers.ParseOptionSymbol(longSym), 1.11m, null),
			new(shortSym, LegAction.Sell, 10, ParsingHelpers.ParseOptionSymbol(shortSym), 0.48m, null)
		};

		var (defaultIdx, defaultErr) = AnalyzeRollCommand.SelectRolledLeg(legs, null, interactive: false);
		Assert.Null(defaultErr);
		Assert.Equal(1, defaultIdx);

		var (longIdx, longErr) = AnalyzeRollCommand.SelectRolledLeg(legs, "long", interactive: false);
		Assert.Null(longErr);
		Assert.Equal(0, longIdx);

		var (symIdx, symErr) = AnalyzeRollCommand.SelectRolledLeg(legs, shortSym, interactive: false);
		Assert.Null(symErr);
		Assert.Equal(1, symIdx);
	}

	[Fact]
	public void Enumerate_MarksSnappedStrikeWhenAnchorStrikeNotListedAtExpiry()
	{
		var today = new DateTime(2026, 11, 19);
		var quotes = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		// Nov 23 has $1 strikes (including 732)
		foreach (var kv in BuildChain("SPY", "C", [new DateTime(2026, 11, 23), new DateTime(2026, 11, 27)], [731m, 732m, 733m]))
			quotes[kv.Key] = kv.Value;
		// Dec 03 only has $5 strikes (730, 735 — no 732)
		foreach (var kv in BuildChain("SPY", "C", [new DateTime(2026, 12, 3)], [725m, 730m, 735m]))
			quotes[kv.Key] = kv.Value;

		var oldSym = MatchKeys.OccSymbol("SPY", new DateTime(2026, 11, 23), 732m, "C");
		var oldParsed = ParsingHelpers.ParseOptionSymbol(oldSym)!;

		var (candidates, _) = RollCandidateEnumerator.Enumerate(
			quotes, oldParsed, oldSym, oldParsed, anchorSymbol: null, LegAction.Sell, strikesN: 0, expN: 2, today: today);

		Assert.Equal(2, candidates.Count);
		var nov27 = Assert.Single(candidates, c => c.Parsed.ExpiryDate.Date == new DateTime(2026, 11, 27));
		Assert.Equal(732m, nov27.Parsed.Strike);
		Assert.False(nov27.IsSnappedStrike);

		var dec03 = Assert.Single(candidates, c => c.Parsed.ExpiryDate.Date == new DateTime(2026, 12, 3));
		Assert.Equal(730m, dec03.Parsed.Strike);
		Assert.True(dec03.IsSnappedStrike);
	}

	[Fact]
	public void FilterAndSort_CreditOnly_EvFallback_AndTop()
	{
		static RollCandidateRow Row(string sym, decimal strike, DateTime exp, decimal rollNet, decimal? creditPerDay, decimal? managedEv) =>
			new(sym, ParsingHelpers.ParseOptionSymbol(sym)!, false, false, false, false, 7, rollNet, rollNet - 0.05m, creditPerDay, -0.05m, 500m, 0m, 200m, strike, 300m, [strike], 0.60m, managedEv, managedEv);

		var exp1 = new DateTime(2026, 4, 24);
		var exp2 = new DateTime(2026, 5, 1);
		var rows = new[]
		{
			Row(MatchKeys.OccSymbol("GME", exp1, 25m, "C"), 25m, exp1, -0.10m, -0.014m, 100m), // debit
			Row(MatchKeys.OccSymbol("GME", exp1, 26m, "C"), 26m, exp1, +0.20m, +0.028m, 50m),
			Row(MatchKeys.OccSymbol("GME", exp2, 26m, "C"), 26m, exp2, +0.40m, +0.029m, 150m),
		};

		var creditOnlyEv = RollComparison.FilterAndSort(rows, creditOnly: true, sortMode: "ev", top: 15);
		Assert.Equal(2, creditOnlyEv.Count);
		Assert.Equal(150m, creditOnlyEv[0].ManagedExpectedValue);

		var top1 = RollComparison.FilterAndSort(rows, creditOnly: false, sortMode: "credit", top: 1);
		Assert.Single(top1);
		Assert.Equal(+0.40m, top1[0].RollNetMid);

		// When no rows have EV, --sort ev falls back to CreditPerDay
		var unscored = rows.Select(r => r with { HoldExpectedValue = null, ManagedExpectedValue = null }).ToList();
		var fallback = RollComparison.FilterAndSort(unscored, creditOnly: true, sortMode: "ev", top: 15);
		Assert.Equal(+0.029m, fallback[0].CreditPerDay);
	}

	[Fact]
	public void BuildTradePlaceCommands_CalendarAndVerticalStayCombo_DiagonalSplits()
	{
		var oldShortSym = "SPY261123C00732000";
		var rolledShort = new RollPositionLeg(oldShortSym, LegAction.Sell, 22, ParsingHelpers.ParseOptionSymbol(oldShortSym), 1.91m, null);

		// Same-strike calendar → 1 combo order
		var calendarSym = "SPY261125C00732000";
		var calCmds = RollGridRenderer.BuildTradePlaceCommands(rolledShort, calendarSym, +1.65m, 1.48m, 1.52m, 3.17m, 3.22m);
		Assert.Single(calCmds);
		Assert.Equal("wa trade place \"buy:SPY261123C00732000:22,sell:SPY261125C00732000:22\" --side sell --limit 1.65", calCmds[0]);

		// Same-expiry vertical → 1 combo order
		var verticalSym = "SPY261123C00727000";
		var vertCmds = RollGridRenderer.BuildTradePlaceCommands(rolledShort, verticalSym, -0.45m, 1.48m, 1.52m, 1.07m, 1.12m);
		Assert.Single(vertCmds);
		Assert.Equal("wa trade place \"buy:SPY261123C00732000:22,sell:SPY261123C00727000:22\" --side buy --limit 0.45", vertCmds[0]);

		// Diagonal (different strike AND different expiry) → 2 single-leg orders priced at mid by default
		var diagSym = "SPY261125C00727000";
		var diagCmds = RollGridRenderer.BuildTradePlaceCommands(rolledShort, diagSym, +3.39m, 1.48m, 1.52m, 4.86m, 4.92m);
		Assert.Equal(2, diagCmds.Count);
		Assert.Equal("wa trade place \"buy:SPY261123C00732000:22\" --limit 1.50", diagCmds[0]);
		Assert.Equal("wa trade place \"sell:SPY261125C00727000:22\" --limit 4.89", diagCmds[1]);

		// Diagonal with --pricing bidask → priced at natural (ask to close short, bid to open short)
		var diagBidAsk = RollGridRenderer.BuildTradePlaceCommands(rolledShort, diagSym, +3.34m, 1.48m, 1.52m, 4.86m, 4.92m, useBidAsk: true);
		Assert.Equal(2, diagBidAsk.Count);
		Assert.Equal("wa trade place \"buy:SPY261123C00732000:22\" --limit 1.52", diagBidAsk[0]);
		Assert.Equal("wa trade place \"sell:SPY261125C00727000:22\" --limit 4.86", diagBidAsk[1]);
	}

	[Fact]
	public void BuildPostRollSpec_AdjustsCostBasisWithRollCredit()
	{
		var oldShortSym = "SPY261123C00732000";
		var newShortSym = "SPY261125C00727000";
		var longSym = "SPY261211C00731000";
		var rolledShort = new RollPositionLeg(oldShortSym, LegAction.Sell, 22, ParsingHelpers.ParseOptionSymbol(oldShortSym), 1.91m, null);
		var staticLegs = new List<RollPositionLeg>
		{
			new(longSym, LegAction.Buy, 22, ParsingHelpers.ParseOptionSymbol(longSym), 10.40m, null)
		};

		// 1.91 original short credit + 3.34 roll credit = 5.25 effective short basis; long stays at 10.40
		var spec = RollGridRenderer.BuildPostRollSpec(rolledShort, newShortSym, staticLegs, +3.34m);
		Assert.Equal("sell:SPY261125C00727000:22@5.25,buy:SPY261211C00731000:22@10.40", spec);

		// When no explicit cost basis is known, falls back to @MID
		var noBasis = RollGridRenderer.BuildPostRollSpec(rolledShort with { ExplicitPrice = null }, newShortSym, [staticLegs[0] with { ExplicitPrice = null }], +3.34m);
		Assert.Equal("sell:SPY261125C00727000:22@MID,buy:SPY261211C00731000:22@MID", noBasis);
	}
}
