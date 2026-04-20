using WebullAnalytics.AI.Rules;

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

	/// <summary>Constructs the default rule set from config.</summary>
	internal static IReadOnlyList<IManagementRule> BuildRules(AIConfig config) => new IManagementRule[]
	{
		new StopLossRule(config.Rules.StopLoss),
		new OpportunisticRollRule(config.Rules.OpportunisticRoll),
		new TakeProfitRule(config.Rules.TakeProfit),
		new DefensiveRollRule(config.Rules.DefensiveRoll),
		new RollShortOnExpiryRule(config.Rules.RollShortOnExpiry)
	};
}
