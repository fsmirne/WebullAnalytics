using WebullAnalytics.AI.Output;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.AI.Rules;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>
/// Runs rules in priority order (1 first). For each position, the first rule to return a
/// non-null proposal wins; lower-priority rules are not evaluated for that position.
/// Emits proposals with cash-reserve tags applied. Handles idempotency fingerprint dedup
/// across consecutive ticks.
/// </summary>
internal sealed class RuleEvaluator
{
	private readonly IReadOnlyList<IManagementRule> _rules;
	private readonly AIConfig _config;
	private readonly Dictionary<string, ProposalFingerprint> _lastFingerprintByPositionKey = new();

	public RuleEvaluator(IReadOnlyList<IManagementRule> rules, AIConfig config)
	{
		_rules = rules.OrderBy(r => r.Priority).ThenBy(r => r.Name).ToList();
		_config = config;
	}

	/// <summary>Result of one evaluation pass.</summary>
	/// <param name="Proposal">The emitted proposal (always non-null when in the list).</param>
	/// <param name="IsRepeat">True when the same fingerprint fired on the previous tick for this position.</param>
	internal readonly record struct EvaluationResult(ManagementProposal Proposal, bool IsRepeat);

	internal IReadOnlyList<EvaluationResult> Evaluate(EvaluationContext ctx)
	{
		var results = new List<EvaluationResult>();

		foreach (var (key, position) in ctx.OpenPositions)
		{
			ManagementProposal? proposal = null;
			foreach (var rule in _rules)
			{
				proposal = rule.Evaluate(position, ctx);
				if (proposal != null) break;
			}
			if (proposal == null) continue;

			proposal = AttachDiagnostic(proposal, position, ctx);

			// Apply cash-reserve tag.
			var check = CashReserveHelper.Check(
				netDebit: proposal.NetDebit,
				currentCash: ctx.AccountCash,
				accountValue: ctx.AccountValue,
				reserveMode: _config.CashReserve.Mode,
				reserveValue: _config.CashReserve.Value);

			if (check.Blocked)
				proposal = proposal with { CashReserveBlocked = true, CashReserveDetail = check.Detail };

			// Dedup.
			var fp = ProposalFingerprint.From(proposal);
			var isRepeat = _lastFingerprintByPositionKey.TryGetValue(key, out var prev) && ProposalFingerprint.AreEquivalent(prev, fp);
			_lastFingerprintByPositionKey[key] = fp;

			results.Add(new EvaluationResult(proposal, isRepeat));
		}

		// Forget fingerprints for positions that no longer exist (avoid unbounded memory growth).
		var stale = _lastFingerprintByPositionKey.Keys.Where(k => !ctx.OpenPositions.ContainsKey(k)).ToList();
		foreach (var k in stale) _lastFingerprintByPositionKey.Remove(k);

		return results;
	}

	private static ManagementProposal AttachDiagnostic(ManagementProposal proposal, OpenPosition position, EvaluationContext ctx)
	{
		if (!ctx.UnderlyingPrices.TryGetValue(position.Ticker, out var spot) || spot <= 0m)
			return proposal;

		var diagLegs = position.Legs
			.Where(l => l.CallPut != null && l.Expiry.HasValue)
			.Select(l => new DiagnosticLeg(
				Symbol: l.Symbol,
				Parsed: new OptionParsed(position.Ticker, l.Expiry!.Value, l.CallPut!, l.Strike),
				IsLong: l.Side == Side.Buy,
				Qty: l.Qty,
				PricePerShare: TryGetMid(ctx.Quotes, l.Symbol),
				CostBasisPerShare: null))
			.ToList();

		if (diagLegs.Count == 0)
			return proposal;

		decimal IvResolver(string symbol)
		{
			if (ctx.Quotes.TryGetValue(symbol, out var q) && q.ImpliedVolatility is decimal iv && iv > 0m)
				return iv;

			var leg = diagLegs.FirstOrDefault(l => l.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
			var mid = TryGetMid(ctx.Quotes, symbol);
			if (leg != null && mid.HasValue)
			{
				var expiryTime = leg.Parsed.ExpiryDate.Date + OptionMath.MarketClose;
				var timeYears = (expiryTime - ctx.Now).TotalDays / 365.0;
				if (timeYears > 0)
					return OptionMath.ImpliedVol(spot, leg.Parsed.Strike, timeYears, OptionMath.RiskFreeRate, mid.Value, leg.Parsed.CallPut);
			}

			return 0.40m;
		}

		var technicalBias = ctx.TechnicalSignals.TryGetValue(position.Ticker, out var signal) ? signal.Score : 0m;
		var diagnostic = RiskDiagnosticBuilder.Build(diagLegs, spot, ctx.Now, IvResolver, trend: null);
		var probe = RiskDiagnosticProbeBuilder.Build(diagLegs, spot, ctx.Now, IvResolver, ctx.Quotes, opener: null, technicalBiasOverride: technicalBias);
		return proposal with { Diagnostic = diagnostic with { Probe = probe } };
	}

	private static decimal? TryGetMid(IReadOnlyDictionary<string, OptionContractQuote> quotes, string symbol)
	{
		if (!quotes.TryGetValue(symbol, out var q) || q.Bid == null || q.Ask == null)
			return null;

		return (q.Bid.Value + q.Ask.Value) / 2m;
	}

	/// <summary>Constructs the default rule set from config.</summary>
	internal static IReadOnlyList<IManagementRule> BuildRules(AIConfig config, string pricingMode = SuggestionPricing.Mid)
	{
		var debug = string.Equals(config.Log.ConsoleVerbosity, "debug", StringComparison.OrdinalIgnoreCase);
		var normalizedPricing = SuggestionPricing.Normalize(pricingMode);
		return new IManagementRule[]
		{
			new StopLossRule(config.Rules.StopLoss),
			new OpportunisticRollRule(config.Rules.OpportunisticRoll, debug, normalizedPricing),
			new TakeProfitRule(config.Rules.TakeProfit),
			new DefensiveRollRule(config.Rules.DefensiveRoll),
			new RollShortOnExpiryRule(config.Rules.RollShortOnExpiry)
		};
	}
}
