using Xunit;
using WebullAnalytics.AI;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenCandidateEvaluatorTests
{
    private sealed class StaticQuoteSource : IQuoteSource
    {
        private readonly QuoteSnapshot _snapshot;
        public StaticQuoteSource(QuoteSnapshot s) { _snapshot = s; }
        public Task<QuoteSnapshot> GetQuotesAsync(DateTime asOf, IReadOnlySet<string> symbols, IReadOnlySet<string> tickers, CancellationToken ct)
            => Task.FromResult(_snapshot);
    }

    private static AIConfig BuildConfig(OpenerConfig opener) => new()
    {
        Tickers = new() { "SPY" },
        Opener = opener,
        CashReserve = new CashReserveConfig { Mode = "absolute", Value = 0m }
    };

    private static EvaluationContext BuildContext(decimal cash, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes) => new(
        Now: new DateTime(2026, 4, 20, 10, 0, 0),
        OpenPositions: new Dictionary<string, OpenPosition>(),
        UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = spot },
        Quotes: quotes,
        AccountCash: cash,
        AccountValue: cash,
        TechnicalSignals: new Dictionary<string, TechnicalBias>()
    );

    [Fact]
    public async Task NoQuotesReturnsNoProposals()
    {
        var cfg = BuildConfig(new OpenerConfig());
        var ctx = BuildContext(cash: 10000m, spot: 500m, quotes: new Dictionary<string, OptionContractQuote>());
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal>()));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.Empty(proposals);
    }

    [Fact]
    public async Task TopNPerTickerIsEnforced()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 2 });
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 100000m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.True(proposals.Count <= 2, $"expected ≤ 2 proposals, got {proposals.Count}");
    }

    [Fact]
    public async Task ZeroCashBlocksSizing()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 5 });
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 0m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.All(proposals, p => Assert.True(p.CashReserveBlocked));
        Assert.All(proposals, p => Assert.Equal(0, p.Qty));
    }

    [Fact]
    public async Task MaxQtyIsClamped()
    {
        var cfg = BuildConfig(new OpenerConfig { TopNPerTicker = 5, MaxQtyPerProposal = 3 });
        var quotes = new FakeUniversalQuotes(0.40m);
        var ctx = BuildContext(cash: 1_000_000m, spot: 500m, quotes: quotes);
        var src = new StaticQuoteSource(new QuoteSnapshot(new Dictionary<string, OptionContractQuote>(), new Dictionary<string, decimal> { ["SPY"] = 500m }));
        var ev = new OpenCandidateEvaluator(cfg, src);
        var proposals = await ev.EvaluateAsync(ctx, default);
        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.True(p.Qty <= 3));
    }

    /// <summary>Fake quote dictionary: returns a constant 1.00/1.10 quote with the given IV for every requested symbol.</summary>
    private sealed class FakeUniversalQuotes : IReadOnlyDictionary<string, OptionContractQuote>
    {
        private readonly decimal _iv;
        public FakeUniversalQuotes(decimal iv) { _iv = iv; }
        public OptionContractQuote this[string key] => TestQuote.Q(1.00m, 1.10m, _iv);
        public IEnumerable<string> Keys => Array.Empty<string>();
        public IEnumerable<OptionContractQuote> Values => Array.Empty<OptionContractQuote>();
        public int Count => 0;
        public bool ContainsKey(string key) => true;
        public IEnumerator<KeyValuePair<string, OptionContractQuote>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, OptionContractQuote>>().GetEnumerator();
        public bool TryGetValue(string key, out OptionContractQuote value) { value = this[key]; return true; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
