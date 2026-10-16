namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Priority 2: close a position that would carry over a market closure (weekend or holiday) in the last
/// <c>minutesBeforeClose</c> of the session before it, regardless of P&L — "don't hold through a gap the
/// position can't react to". Optionally gated on the day's VIX move: with <c>minVixChangePct</c> &gt; 0 it fires
/// only when VIX is up at least that fraction from the prior session's close. A position whose legs all expire
/// today never carries over and is left to settlement.
/// </summary>
internal sealed class WeekendCloseRule : IManagementRule
{
	private readonly WeekendCloseConfig _config;

	public WeekendCloseRule(WeekendCloseConfig config) { _config = config; }

	public string Name => "WeekendCloseRule";
	public int Priority => 2;

	public ManagementProposal? Evaluate(OpenPosition position, EvaluationContext ctx)
	{
		if (!_config.Enabled) return null;
		if (position.Quantity <= 0) return null;
		var today = ctx.Now.Date;
		if (!MarketCalendar.IsOpen(today) || (MarketCalendar.NextOpenAfter(today) - today).Days <= 1) return null;
		if (ctx.Now.TimeOfDay < MarketCalendar.CloseTimeEt(today) - TimeSpan.FromMinutes(_config.MinutesBeforeClose)) return null;
		if (!position.Legs.Any(l => l.Expiry is { } exp && exp.Date > today)) return null;

		var vixTag = "";
		if (_config.MinVixChangePct > 0m)
		{
			if (ctx.Vix is not { } vix || ctx.VixPriorClose is not { } prior || prior <= 0m) return null;
			var change = vix / prior - 1m;
			if (change < _config.MinVixChangePct) return null;
			vixTag = $", VIX {vix:F2} {change:+0.0%;-0.0%} vs prior close {prior:F2}";
		}

		var legs = position.Legs.Select(l => new ProposalLeg(Action: l.Side == Side.Buy ? "sell" : "buy", Symbol: l.Symbol, Qty: l.Qty)).ToList();
		return new ManagementProposal(
			Rule: "WeekendCloseRule",
			Ticker: position.Ticker,
			PositionKey: position.Key,
			Kind: ProposalKind.Close,
			Legs: legs,
			NetDebit: 0m,
			Rationale: $"market closed until {MarketCalendar.NextOpenAfter(today):ddd yyyy-MM-dd}{vixTag}, close all {position.Quantity} before the gap"
		);
	}
}
