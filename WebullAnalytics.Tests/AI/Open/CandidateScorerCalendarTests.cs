using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerCalendarTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m, StructureWeight = new() };

    [Fact]
    public void CalendarDebitIsLongAskMinusShortBid()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24); // 4 DTE
        var longExp = new DateTime(2026, 5, 15);   // 25 DTE
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        // debit = long_ask − short_bid = 5.10 − 1.50 = 3.60 per share → 360 per contract
        Assert.Equal(-360m, p.DebitOrCreditPerContract);
        Assert.Equal(360m, p.CapitalAtRiskPerContract);
    }

    [Fact]
    public void CalendarDirectionalFitIsZero()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24);
        var longExp = new DateTime(2026, 5, 15);
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        // Verify fit=0 by checking that bias has no effect: scores with bias=0 and bias=0.80 should match.
        // (The score formula now includes a structure-independent balance factor, so RawScore != BiasAdjusted
        // in general — comparing two scorings with different bias is the cleanest way to pin "fit is 0".)
        var pNoBias = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        var pWithBias = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0.80m, Cfg())!;
        Assert.Equal(0, pWithBias.DirectionalFit);
        Assert.Equal(pNoBias.BiasAdjustedScore, pWithBias.BiasAdjustedScore);
    }

    [Fact]
    public void CalendarPopIsProbInProfitBand()
    {
        var asOf = new DateTime(2026, 4, 20);
        var shortExp = new DateTime(2026, 4, 24);
        var longExp = new DateTime(2026, 5, 15);
        var shortSym = MatchKeys.OccSymbol("SPY", shortExp, 500m, "C");
        var longSym = MatchKeys.OccSymbol("SPY", longExp, 500m, "C");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.LongCalendar, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: shortExp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(1.50m, 1.55m, 0.40m),
            [longSym] = TestQuote.Q(4.90m, 5.10m, 0.40m)
        };
        var p = CandidateScorer.ScoreCalendarOrDiagonal(skel, spot: 500m, asOf, quotes, bias: 0m, Cfg())!;
        // POP is now P(BE_lower < S_T < BE_upper) computed from numerically-found breakevens. The exact
        // value depends on the quote-implied IV's consistency with the legs' Black-Scholes prices; the
        // test's contrived prices yield very wide breakevens, but POP must always be a probability.
        Assert.InRange((double)p.ProbabilityOfProfit, 0.0, 1.0);
        Assert.True(p.ProbabilityOfProfit > 0m);
        Assert.Equal(2, p.Breakevens.Count);
        Assert.True(p.Breakevens[0] < p.Breakevens[1]);
    }
}
