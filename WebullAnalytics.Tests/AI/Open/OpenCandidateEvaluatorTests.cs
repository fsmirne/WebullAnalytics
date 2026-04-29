using WebullAnalytics.AI;
using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.Sources;
using Xunit;

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

	private static AIConfig BuildConfig(OpenerConfig opener)
	{
		opener.StrikeSteps["SPY"] = 1m;
		return new AIConfig
		{
			Tickers = new() { "SPY" },
			Opener = opener,
			CashReserve = new CashReserveConfig { Mode = "absolute", Value = 0m }
		};
	}

	private static EvaluationContext BuildContext(decimal cash, decimal spot, IReadOnlyDictionary<string, OptionContractQuote> quotes) => new(
		Now: new DateTime(2026, 4, 20, 10, 0, 0),
		OpenPositions: new Dictionary<string, OpenPosition>(),
		UnderlyingPrices: new Dictionary<string, decimal> { ["SPY"] = spot },
		Quotes: quotes,
		AccountCash: cash,
		AccountValue: cash,
		TechnicalSignals: new Dictionary<string, TechnicalBias>()
	);

	private static OpenProposal MakeProposal(OpenStructureKind kind, int daysToTarget, decimal score, string fingerprint, decimal? thetaPerDayPerContract = null) => new(
		Ticker: "SPY",
		StructureKind: kind,
		Legs: new[] { new ProposalLeg("buy", "SPY   260515C00500000", 1) },
		Qty: 1,
		DebitOrCreditPerContract: -500m,
		MaxProfitPerContract: 1000m,
		MaxLossPerContract: -500m,
		CapitalAtRiskPerContract: 500m,
		Breakevens: new[] { 505m },
		ProbabilityOfProfit: 0.45m,
		ExpectedValuePerContract: 25m,
		DaysToTarget: daysToTarget,
		RawScore: score,
		BiasAdjustedScore: score,
		DirectionalFit: 0,
		Rationale: "test rationale",
		Fingerprint: fingerprint,
		ThetaPerDayPerContract: thetaPerDayPerContract,
		FinalScore: score * CandidateScorer.ComputeThetaFactor(thetaPerDayPerContract, 500m)
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

	[Fact]
	public void RankForOutputUsesFinalScoreForCalendars()
	{
		var currentWeek = MakeProposal(OpenStructureKind.LongCalendar, daysToTarget: 4, score: 0.40m, fingerprint: "near", thetaPerDayPerContract: 4.00m);
		var nextWeek = MakeProposal(OpenStructureKind.LongCalendar, daysToTarget: 11, score: 1.00m, fingerprint: "far", thetaPerDayPerContract: 1.00m);

		var ranked = OpenCandidateEvaluator.RankForOutput(new[] { nextWeek, currentWeek });

		Assert.Equal(new[] { "far", "near" }, ranked.Select(p => p.Fingerprint).ToArray());
	}

	[Fact]
	public void RankForOutputUsesFinalScoreForDoubleDiagonal()
	{
		var near = MakeProposal(OpenStructureKind.DoubleDiagonal, daysToTarget: 4, score: 0.40m, fingerprint: "near", thetaPerDayPerContract: 4.00m);
		var far = MakeProposal(OpenStructureKind.DoubleDiagonal, daysToTarget: 11, score: 1.00m, fingerprint: "far", thetaPerDayPerContract: 1.00m);

		var ranked = OpenCandidateEvaluator.RankForOutput(new[] { far, near });

		Assert.Equal(new[] { "far", "near" }, ranked.Select(p => p.Fingerprint).ToArray());
	}

	[Fact]
	public void RankForOutputUsesFinalScoreWhenCalendarThetaIsComparable()
	{
		var currentWeek = MakeProposal(OpenStructureKind.LongDiagonal, daysToTarget: 4, score: 1.00m, fingerprint: "near", thetaPerDayPerContract: 1.00m);
		var nextWeek = MakeProposal(OpenStructureKind.LongDiagonal, daysToTarget: 11, score: 1.30m, fingerprint: "far", thetaPerDayPerContract: 1.10m);

		var ranked = OpenCandidateEvaluator.RankForOutput(new[] { currentWeek, nextWeek });

		Assert.Equal(new[] { "far", "near" }, ranked.Select(p => p.Fingerprint).ToArray());
	}

	[Fact]
	public void RankForOutputUsesFinalScoreForDoubleCalendar()
	{
		var near = MakeProposal(OpenStructureKind.DoubleCalendar, daysToTarget: 4, score: 0.40m, fingerprint: "near", thetaPerDayPerContract: 4.00m);
		var far = MakeProposal(OpenStructureKind.DoubleCalendar, daysToTarget: 11, score: 1.00m, fingerprint: "far", thetaPerDayPerContract: 1.00m);

		var ranked = OpenCandidateEvaluator.RankForOutput(new[] { far, near });

		Assert.Equal(new[] { "far", "near" }, ranked.Select(p => p.Fingerprint).ToArray());
	}

	[Fact]
	public void RankForOutputUsesFinalScoreForVerticals()
	{
		var highTheta = MakeProposal(OpenStructureKind.ShortPutVertical, daysToTarget: 4, score: 0.40m, fingerprint: "high-theta", thetaPerDayPerContract: 3.00m);
		var highScore = MakeProposal(OpenStructureKind.ShortPutVertical, daysToTarget: 10, score: 1.00m, fingerprint: "high-score", thetaPerDayPerContract: 1.00m);

		var ranked = OpenCandidateEvaluator.RankForOutput(new[] { highScore, highTheta });

		Assert.Equal(new[] { "high-score", "high-theta" }, ranked.Select(p => p.Fingerprint).ToArray());
	}

	[Fact]
	public void ExpandExecutableProposalsSplitsDoubleCalendarIntoTwoCalendars()
	{
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortPut = MatchKeys.OccSymbol("SPY", shortExp, 495m, "P");
		var longPut = MatchKeys.OccSymbol("SPY", longExp, 495m, "P");
		var shortCall = MatchKeys.OccSymbol("SPY", shortExp, 505m, "C");
		var longCall = MatchKeys.OccSymbol("SPY", longExp, 505m, "C");
		var proposal = new OpenProposal(
			Ticker: "SPY",
			StructureKind: OpenStructureKind.DoubleCalendar,
			Legs: new[]
			{
				new ProposalLeg("sell", shortPut, 2, 1.00m, 0.95m),
				new ProposalLeg("buy", longPut, 2, 1.50m, 1.55m),
				new ProposalLeg("sell", shortCall, 2, 1.00m, 0.95m),
				new ProposalLeg("buy", longCall, 2, 1.50m, 1.55m)
			},
			Qty: 2,
			DebitOrCreditPerContract: -100m,
			MaxProfitPerContract: 50m,
			MaxLossPerContract: -100m,
			CapitalAtRiskPerContract: 100m,
			Breakevens: new[] { 495m, 505m },
			ProbabilityOfProfit: 0.40m,
			ExpectedValuePerContract: 10m,
			DaysToTarget: 4,
			RawScore: 0.10m,
			BiasAdjustedScore: 0.10m,
			DirectionalFit: 0,
			Rationale: "double calendar",
			Fingerprint: "dc",
			ThetaPerDayPerContract: 2m);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortPut] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[longPut] = TestQuote.Q(1.45m, 1.55m, 0.40m),
			[shortCall] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[longCall] = TestQuote.Q(1.45m, 1.55m, 0.40m)
		};

		var cfg = new OpenerConfig();
		cfg.StrikeSteps["SPY"] = 1m;
		var expanded = OpenCandidateEvaluator.ExpandExecutableProposals(proposal, 500m, new DateTime(2026, 4, 20), quotes, 0m, cfg, null, SuggestionPricing.Mid);

		Assert.Equal(2, expanded.Count);
		Assert.All(expanded, p => Assert.Equal(OpenStructureKind.LongCalendar, p.StructureKind));
		Assert.All(expanded, p => Assert.Equal(2, p.Qty));
		Assert.All(expanded, p => Assert.Equal(2, p.Legs.Count));
	}

	[Fact]
	public void ExpandExecutableProposalsSplitsDoubleDiagonalIntoTwoDiagonals()
	{
		var shortExp = new DateTime(2026, 4, 24);
		var longExp = new DateTime(2026, 5, 15);
		var shortPut = MatchKeys.OccSymbol("SPY", shortExp, 495m, "P");
		var longPut = MatchKeys.OccSymbol("SPY", longExp, 494m, "P");
		var shortCall = MatchKeys.OccSymbol("SPY", shortExp, 505m, "C");
		var longCall = MatchKeys.OccSymbol("SPY", longExp, 506m, "C");
		var proposal = new OpenProposal(
			Ticker: "SPY",
			StructureKind: OpenStructureKind.DoubleDiagonal,
			Legs: new[]
			{
				new ProposalLeg("sell", shortPut, 1, 1.00m, 0.95m),
				new ProposalLeg("buy", longPut, 1, 1.60m, 1.65m),
				new ProposalLeg("sell", shortCall, 1, 1.00m, 0.95m),
				new ProposalLeg("buy", longCall, 1, 1.60m, 1.65m)
			},
			Qty: 1,
			DebitOrCreditPerContract: -120m,
			MaxProfitPerContract: 80m,
			MaxLossPerContract: -120m,
			CapitalAtRiskPerContract: 120m,
			Breakevens: new[] { 495m, 505m },
			ProbabilityOfProfit: 0.35m,
			ExpectedValuePerContract: 8m,
			DaysToTarget: 4,
			RawScore: 0.06m,
			BiasAdjustedScore: 0.06m,
			DirectionalFit: 0,
			Rationale: "double diagonal",
			Fingerprint: "dd",
			ThetaPerDayPerContract: 1m);
		var quotes = new Dictionary<string, OptionContractQuote>
		{
			[shortPut] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[longPut] = TestQuote.Q(1.55m, 1.65m, 0.40m),
			[shortCall] = TestQuote.Q(0.95m, 1.05m, 0.40m),
			[longCall] = TestQuote.Q(1.55m, 1.65m, 0.40m)
		};

		var cfg = new OpenerConfig();
		cfg.StrikeSteps["SPY"] = 1m;
		var expanded = OpenCandidateEvaluator.ExpandExecutableProposals(proposal, 500m, new DateTime(2026, 4, 20), quotes, 0m, cfg, null, SuggestionPricing.Mid);

		Assert.Equal(2, expanded.Count);
		Assert.All(expanded, p => Assert.Equal(OpenStructureKind.LongDiagonal, p.StructureKind));
		Assert.All(expanded, p => Assert.Equal(1, p.Qty));
		Assert.All(expanded, p => Assert.Equal(2, p.Legs.Count));
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
