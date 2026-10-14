using WebullAnalytics.AI;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class RealizedVolModelTests
{
	private static YahooOptionsClient.HistoricalBar Bar(DateTime d, decimal open, decimal close) => new(d, open, Math.Max(open, close), Math.Min(open, close), close, close, null);

	private static readonly RealizedVolModel Model = new(OvernightVariance: 0.0001, SessionVariance: 0.0004);

	[Fact]
	public void FromBars_SplitsOvernightGapFromSessionMove()
	{
		var bars = new[] { Bar(new(2026, 10, 5), 100m, 100m), Bar(new(2026, 10, 6), 101m, 102m), Bar(new(2026, 10, 7), 101m, 100m) };
		var m = RealizedVolModel.FromBars(bars)!;
		// EWMA: the newest session weighs 1, the one before it 0.5^(1/half-life).
		var d = Math.Pow(0.5, 1.0 / RealizedVolModel.HalfLifeSessions);
		var expectedOvernight = (Math.Pow(Math.Log(101.0 / 102.0), 2) + d * Math.Pow(Math.Log(101.0 / 100.0), 2)) / (1 + d);
		var expectedSession = (Math.Pow(Math.Log(100.0 / 101.0), 2) + d * Math.Pow(Math.Log(102.0 / 101.0), 2)) / (1 + d);
		Assert.Equal(expectedOvernight, m.OvernightVariance, 12);
		Assert.Equal(expectedSession, m.SessionVariance, 12);
	}

	[Fact]
	public void FromBars_TooShortIsNull() => Assert.Null(RealizedVolModel.FromBars(new[] { Bar(new(2026, 10, 5), 100m, 100m) }));

	[Fact]
	public void ZeroDte_MidSession_IsTheUnelapsedSessionFraction()
	{
		// Wed 2026-10-14 12:45 ET: 195 of 390 session minutes left, no gap left today.
		Assert.Equal(0.0004 * 0.5, Model.VarianceTo(new DateTime(2026, 10, 14, 12, 45, 0), new DateTime(2026, 10, 14)), 12);
	}

	[Fact]
	public void BeforeTheOpen_IncludesTodaysGap()
	{
		Assert.Equal(0.0001 + 0.0004, Model.VarianceTo(new DateTime(2026, 10, 14, 8, 0, 0), new DateTime(2026, 10, 14)), 12);
	}

	[Fact]
	public void AfterTheClose_TodayContributesNothing()
	{
		Assert.Equal(0.0, Model.VarianceTo(new DateTime(2026, 10, 14, 16, 30, 0), new DateTime(2026, 10, 14)), 12);
	}

	[Fact]
	public void MultiDay_CountsEachLaterTradingSessionOnce_SkippingTheWeekend()
	{
		// Fri 2026-10-16 at the open → Mon 2026-10-19 close: Friday's full session + Monday's gap and session.
		var v = Model.VarianceTo(new DateTime(2026, 10, 16, 9, 30, 0), new DateTime(2026, 10, 19));
		Assert.Equal(0.0004 + (0.0001 + 0.0004), v, 12);
	}

	[Fact]
	public void EarlyClose_UsesTheShortSession()
	{
		// Fri 2026-11-27 (day after Thanksgiving) closes 13:00: at 11:15, 105 of 210 minutes remain.
		Assert.Equal(0.0004 * 0.5, Model.VarianceTo(new DateTime(2026, 11, 27, 11, 15, 0), new DateTime(2026, 11, 27)), 12);
	}

	[Fact]
	public void ScenarioVol_IsTheForecastOnTheIvClock()
	{
		var asOf = new DateTime(2026, 10, 14, 12, 45, 0);
		var years = 3.25 / 24.0 / 365.0;
		var vol = Model.ScenarioVol(asOf, new DateTime(2026, 10, 14), years)!.Value;
		Assert.Equal(Math.Sqrt(0.0002), (double)vol * Math.Sqrt(years), 9);
	}
}
