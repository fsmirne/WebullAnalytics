using WebullAnalytics.AI;
using WebullAnalytics.AI.Rules;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class ManagementAutoExecutorTests
{
	private static ManagementAutoExecuteConfig DefaultConfig() => new()
	{
		Enabled = true,
		Submit = false, // dry-run keeps tests deterministic and broker-free
		Rules = new List<string> { "CloseBeforeShortExpiryRule" },
		ScaleOut = new ScaleOutConfig
		{
			Tz = "America/New_York",
			Tranche1Start = "10:00", Tranche1End = "10:30",
			Tranche2Start = "12:30", Tranche2End = "13:00",
			Tranche3Start = "15:00", Tranche3End = "15:30",
			Tranche1Fraction = 0.3333m,
			Tranche2Fraction = 0.5m,
			MinQty = 100,
		}
	};

	private static OpenPosition GmePutCalendar(int qty) => new(
		Key: "GME_CALENDAR_25.00_20260501",
		Ticker: "GME",
		StrategyKind: "CALENDAR",
		Legs: new[]
		{
			new PositionLeg("GME260501P00025000", Side.Sell, 25.00m, new DateTime(2026, 5, 1), "P", qty),
			new PositionLeg("GME260515P00025000", Side.Buy,  25.00m, new DateTime(2026, 5, 15), "P", qty),
		},
		InitialNetDebit: 0.50m,
		AdjustedNetDebit: 0.50m,
		Quantity: qty);

	private static ManagementProposal CloseProposal(OpenPosition p, bool emergency = false) =>
		new(
			Rule: "CloseBeforeShortExpiryRule",
			Ticker: p.Ticker,
			PositionKey: p.Key,
			Kind: ProposalKind.Close,
			Legs: p.Legs.Select(l => new ProposalLeg(l.Side == Side.Buy ? "sell" : "buy", l.Symbol, l.Qty)).ToList(),
			NetDebit: 0m,
			Rationale: emergency ? "[emergency] spot past BE" : "expiry day, profit ≥ threshold");

	private static EvaluationContext CtxAtEt(int hour, int minute, OpenPosition p)
	{
		var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		var localDt = new DateTime(2026, 5, 1, hour, minute, 0, DateTimeKind.Unspecified);
		var utc = TimeZoneInfo.ConvertTimeToUtc(localDt, et);
		return new EvaluationContext(
			Now: utc.ToLocalTime(),
			OpenPositions: new Dictionary<string, OpenPosition> { [p.Key] = p },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501P00025000"] = new("GME260501P00025000", null, 0.10m, 0.14m, null, null, 100, 1000, 0.40m),
				["GME260515P00025000"] = new("GME260515P00025000", null, 0.85m, 0.89m, null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
	}

	private static IReadOnlyList<RuleEvaluator.EvaluationResult> Results(ManagementProposal p) =>
		new[] { new RuleEvaluator.EvaluationResult(p, IsRepeat: false) };

	[Fact]
	public async Task Disabled_DoesNothing()
	{
		var cfg = DefaultConfig();
		cfg.Enabled = false;
		var exec = new ManagementAutoExecutor(cfg, account: null);
		var pos = GmePutCalendar(300);
		var count = await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(10, 5, pos), CancellationToken.None);
		Assert.Equal(0, count);
	}

	[Fact]
	public async Task IgnoresProposalsNotInAllowList()
	{
		var exec = new ManagementAutoExecutor(DefaultConfig(), account: null);
		var pos = GmePutCalendar(300);
		var stopLossProposal = CloseProposal(pos) with { Rule = "StopLossRule" };
		var count = await exec.HandleAsync(Results(stopLossProposal), CtxAtEt(10, 5, pos), CancellationToken.None);
		Assert.Equal(0, count);
	}

	[Fact]
	public async Task Tranche1_FiresOnceInWindow()
	{
		var exec = new ManagementAutoExecutor(DefaultConfig(), account: null);
		var pos = GmePutCalendar(300);

		// Tick 1 inside T1 window — fires.
		Assert.Equal(1, await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(10, 5, pos), CancellationToken.None));
		// Tick 2 still inside T1 window — silent.
		Assert.Equal(0, await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(10, 20, pos), CancellationToken.None));
	}

	[Fact]
	public async Task OutsideAllWindows_DoesNothing()
	{
		var exec = new ManagementAutoExecutor(DefaultConfig(), account: null);
		var pos = GmePutCalendar(300);
		Assert.Equal(0, await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(11, 0, pos), CancellationToken.None));
	}

	[Fact]
	public async Task EmergencyProposal_BypassesScheduleAndFiresOutsideAnyWindow()
	{
		var exec = new ManagementAutoExecutor(DefaultConfig(), account: null);
		var pos = GmePutCalendar(300);
		var emergency = CloseProposal(pos, emergency: true);
		// 11:00 ET is between T1 and T2 — emergency still fires.
		Assert.Equal(1, await exec.HandleAsync(Results(emergency), CtxAtEt(11, 0, pos), CancellationToken.None));
		// Idempotent: second tick same day — silent.
		Assert.Equal(0, await exec.HandleAsync(Results(emergency), CtxAtEt(11, 5, pos), CancellationToken.None));
	}

	[Fact]
	public async Task BelowMinQty_FiresSingleShotOutsideWindows()
	{
		var cfg = DefaultConfig();
		cfg.ScaleOut.MinQty = 100;
		var exec = new ManagementAutoExecutor(cfg, account: null);
		var pos = GmePutCalendar(50); // below minQty
		// 11:00 ET is outside any tranche window, but small position fires immediately.
		Assert.Equal(1, await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(11, 0, pos), CancellationToken.None));
		// Same day — already fired single-shot — silent.
		Assert.Equal(0, await exec.HandleAsync(Results(CloseProposal(pos)), CtxAtEt(12, 0, pos), CancellationToken.None));
	}

	[Fact]
	public async Task ResetsBookkeepingOnDateChange()
	{
		var exec = new ManagementAutoExecutor(DefaultConfig(), account: null);
		var pos = GmePutCalendar(300);

		// Day 1 emergency fires.
		Assert.Equal(1, await exec.HandleAsync(Results(CloseProposal(pos, emergency: true)), CtxAtEt(11, 0, pos), CancellationToken.None));

		// Same day, same emergency proposal — silent.
		Assert.Equal(0, await exec.HandleAsync(Results(CloseProposal(pos, emergency: true)), CtxAtEt(11, 5, pos), CancellationToken.None));

		// Next-day tick: build a context one day later in ET.
		var et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
		var nextDayLocal = new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Unspecified);
		var nextDayUtc = TimeZoneInfo.ConvertTimeToUtc(nextDayLocal, et);
		var nextDayCtx = new EvaluationContext(
			Now: nextDayUtc.ToLocalTime(),
			OpenPositions: new Dictionary<string, OpenPosition> { [pos.Key] = pos },
			UnderlyingPrices: new Dictionary<string, decimal> { ["GME"] = 25.00m },
			Quotes: new Dictionary<string, OptionContractQuote>
			{
				["GME260501P00025000"] = new("GME260501P00025000", null, 0.10m, 0.14m, null, null, 100, 1000, 0.40m),
				["GME260515P00025000"] = new("GME260515P00025000", null, 0.85m, 0.89m, null, null, 100, 1000, 0.40m),
			},
			AccountCash: 0m, AccountValue: 0m,
			TechnicalSignals: new Dictionary<string, TechnicalBias>());
		// State reset for the new ET date — the same emergency proposal fires again.
		Assert.Equal(1, await exec.HandleAsync(Results(CloseProposal(pos, emergency: true)), nextDayCtx, CancellationToken.None));
	}
}
