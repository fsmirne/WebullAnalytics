using WebullAnalytics.AI;
using WebullAnalytics.AI.Sources;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

// A layer with every opener structure disabled (one that opens only signal-driven trades, e.g. opener.cascade) has nothing to enumerate:
// the evaluator must not fetch a chain every tick to build zero candidates.
public class OpenCandidateEvaluatorNoStructuresTests
{
	private sealed class CountingQuoteSource : IQuoteSource
	{
		public int Calls;
		public Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> optionSymbols, IReadOnlySet<string> tickers, CancellationToken cancellation, QuoteOverrides overrides = default)
		{
			Calls++;
			return Task.FromResult(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPXW"] = 7500m }));
		}
	}

	private static AIConfig Config(bool anyStructure)
	{
		var opener = new OpenerConfig();
		var st = opener.Structures;
		st.LongCalendar.Enabled = st.DoubleCalendar.Enabled = st.LongDiagonal.Enabled = st.DoubleDiagonal.Enabled = st.IronButterfly.Enabled = st.IronCondor.Enabled = st.Condor.Enabled = false;
		st.ShortVertical.Enabled = st.LongCallPut.Enabled = st.LongVertical.Enabled = st.DiagonalVertical.Enabled = st.CalendarVertical.Enabled = st.GravityFly.Enabled = false;
		st.ShortVertical.Enabled = anyStructure;
		var ai = new AIConfig { Ticker = "SPXW", Indicators = new IndicatorsConfig { IvDefaultPct = 0.18m, StrikeStep = 5m }, Opener = opener, CashReserve = new CashReserveConfig { Mode = "absolute", Value = 0m } };
		opener.Indicators = ai.Indicators;
		return ai;
	}

	private static EvaluationContext FlatContext() => new(
		Now: new DateTime(2026, 11, 10, 10, 45, 0),
		OpenPositions: new Dictionary<string, OpenPosition>(),
		UnderlyingPrices: new Dictionary<string, decimal>(),
		Quotes: new Dictionary<string, OptionContractQuote>(),
		AccountCash: 100_000m, AccountValue: 100_000m,
		TechnicalSignals: new Dictionary<string, TechnicalBias>());

	[Fact]
	public async Task NoEnabledStructure_FetchesNothing()
	{
		var quotes = new CountingQuoteSource();
		Assert.Empty(await new OpenCandidateEvaluator(Config(anyStructure: false), quotes).EvaluateAsync(FlatContext(), default));
		Assert.Equal(0, quotes.Calls);
	}

	[Fact]
	public async Task AnEnabledStructure_StillProbesTheChain()
	{
		var quotes = new CountingQuoteSource();
		await new OpenCandidateEvaluator(Config(anyStructure: true), quotes).EvaluateAsync(FlatContext(), default);
		Assert.True(quotes.Calls > 0);
	}
}
