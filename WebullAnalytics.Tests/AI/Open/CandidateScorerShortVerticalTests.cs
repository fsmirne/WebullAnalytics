using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class CandidateScorerShortVerticalTests
{
    private static OpenerConfig Cfg() => new() { IvDefaultPct = 40m, DirectionalFitWeight = 0.5m, ProfitBandPct = 5m, StructureWeight = new() };

    private static (CandidateSkeleton skel, Dictionary<string, OptionContractQuote> quotes) PutCreditSpread()
    {
        // SPY put credit: sell 495P / buy 494P, 4 DTE (Mon → Fri)
        var exp = new DateTime(2026, 4, 24);
        var shortSym = MatchKeys.OccSymbol("SPY", exp, 495m, "P");
        var longSym = MatchKeys.OccSymbol("SPY", exp, 494m, "P");
        var skel = new CandidateSkeleton("SPY", OpenStructureKind.ShortPutVertical, new[]
        {
            new ProposalLeg("sell", shortSym, 1),
            new ProposalLeg("buy", longSym, 1)
        }, TargetExpiry: exp);
        var quotes = new Dictionary<string, OptionContractQuote>
        {
            [shortSym] = TestQuote.Q(0.60m, 0.65m, 0.40m),
            [longSym] = TestQuote.Q(0.18m, 0.22m, 0.40m)
        };
        return (skel, quotes);
    }

    [Fact]
    public void PutCreditSpreadCreditIsShortBidMinusLongAsk()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        // credit = (0.60 − 0.22) × 100 = 38
        Assert.Equal(38m, p.DebitOrCreditPerContract);
    }

    [Fact]
    public void PutCreditSpreadCapitalAtRiskIsWidthMinusCredit()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        // width × 100 − credit = 1.0 × 100 − 38 = 62
        Assert.Equal(62m, p.CapitalAtRiskPerContract);
    }

    [Fact]
    public void PutCreditSpreadMaxProfitIsCredit()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(38m, p.MaxProfitPerContract);
    }

    [Fact]
    public void PutCreditSpreadMaxLossIsNegativeRisk()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(-62m, p.MaxLossPerContract);
    }

    [Fact]
    public void PutCreditSpreadBreakevenIsShortStrikeMinusCreditPerShare()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(494.62m, p.Breakevens[0]); // 495 - 0.38
    }

    [Fact]
    public void PutCreditSpreadDirectionalFitIsPositiveOne()
    {
        var (skel, quotes) = PutCreditSpread();
        var p = CandidateScorer.ScoreShortVertical(skel, spot: 500m, asOf: new DateTime(2026, 4, 20), quotes, bias: 0m, Cfg())!;
        Assert.Equal(1, p.DirectionalFit);
    }
}
