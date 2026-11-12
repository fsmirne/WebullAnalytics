using WebullAnalytics;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>The intraday shape of session variance: how much of it is left at a point in the session, and how the realized-vol model
/// uses it for the unelapsed part of today.</summary>
public class IntradayVarianceProfileTests
{
	private const int Points = IntradayVarianceProfile.SessionMinutes / IntradayVarianceProfile.StepMinutes + 1;

	/// <summary>Half the session's variance is gone by 10:00 (two checkpoints in), the rest spreads evenly to the close.</summary>
	private static IntradayVarianceProfile FrontLoaded()
	{
		var remaining = new double[Points];
		for (var i = 0; i < Points; i++) remaining[i] = i <= 2 ? 1.0 - 0.25 * i : 0.5 * (Points - 1 - i) / (Points - 3);
		return new IntradayVarianceProfile(remaining);
	}

	[Fact]
	public void RemainingShare_InterpolatesBetweenCheckpoints()
	{
		var p = FrontLoaded();
		Assert.Equal(1.0, p.RemainingShare(0.0), 12);
		Assert.Equal(0.875, p.RemainingShare(7.5 / 390.0), 12);
		Assert.Equal(0.5, p.RemainingShare(30.0 / 390.0), 12);
		Assert.Equal(0.0, p.RemainingShare(1.0), 12);
	}

	[Fact]
	public void ElapsedAt_InvertsRemainingShare()
	{
		var p = FrontLoaded();
		foreach (var f in new[] { 0.0, 0.01, 0.05, 0.2, 0.5, 0.9, 1.0 })
			Assert.Equal(p.RemainingShare(f), p.RemainingShare(p.ElapsedAt(p.RemainingShare(f))), 9);
		Assert.Equal(30.0 / 390.0, p.ElapsedAt(0.5), 9);
	}

	[Fact]
	public void Checkpoints_AreMadeNonIncreasing()
	{
		var noisy = Enumerable.Range(0, Points).Select(i => 1.0 - (double)i / (Points - 1)).ToArray();
		noisy[5] = 0.95;   // a later checkpoint with more variance left than an earlier one (sampling noise)
		var p = new IntradayVarianceProfile(noisy);
		Assert.True(p.RemainingShare(75.0 / 390.0) <= p.RemainingShare(60.0 / 390.0));
	}

	/// <summary>0DTE at 10:00 with half the session's variance already spent: the model forecasts half a session, not the 92% of the
	/// session's minutes still ahead.</summary>
	[Fact]
	public void RealizedVolModel_UsesTheProfileForTodaysUnelapsedSession()
	{
		var model = new RealizedVolModel(OvernightVariance: 0.0001, SessionVariance: 0.0004, Profile: FrontLoaded());
		var at10 = new DateTime(2026, 10, 14, 10, 0, 0);
		Assert.Equal(0.0004 * 0.5, model.VarianceTo(at10, new DateTime(2026, 10, 14)), 12);
		Assert.Equal(0.0004 * 0.5 + 0.0001 + 0.0004, model.VarianceTo(at10, new DateTime(2026, 10, 15)), 12);
		// Half of what is left accrues by the profile's own clock: a quarter of the session, i.e. halfway through the even tail after 10:00.
		Assert.Equal(new DateTime(2026, 10, 14, 13, 0, 0), model.TimeAtVarianceFraction(at10, new DateTime(2026, 10, 14), 0.5));
	}

	[Fact]
	public void SessionReturns_ReadsCheckpointsFromStartOfBarLabels()
	{
		var day = new DateOnly(2026, 10, 14);
		var bars = new List<MinuteBar>();
		for (var m = 0; m < IntradayVarianceProfile.SessionMinutes; m++)
		{
			var et = day.ToDateTime(new TimeOnly(9, 30)).AddMinutes(m);
			var utc = TimeZoneInfo.ConvertTimeToUtc(et, MarketTime.Zone);
			bars.Add(new MinuteBar(new DateTimeOffset(utc), 100m + m, 100m + m, 100m + m, 101m + m, 1));
		}
		var v = IntradayVarianceProfile.SessionReturns(day, bars)!;
		Assert.Equal(Math.Pow(Math.Log(490.0 / 100.0), 2), v[0], 12);   // open of the 09:30 bar
		Assert.Equal(Math.Pow(Math.Log(490.0 / 115.0), 2), v[1], 12);   // close of the 09:44 bar = price at 09:45
	}

	[Fact]
	public void Fit_NeedsEnoughSessionsBeforeTheDay()
	{
		var sessions = new SortedList<DateOnly, double[]>();
		var shape = Enumerable.Range(0, Points - 1).Select(i => 1.0 - (double)i / (Points - 1)).ToArray();
		for (var i = 0; i < IntradayVarianceProfile.MinSessions; i++) sessions[new DateOnly(2024, 1, 1).AddDays(i)] = shape;
		var last = sessions.Keys[^1];
		Assert.Null(IntradayVarianceProfile.Fit(sessions, last));   // the day itself is excluded → one short
		Assert.NotNull(IntradayVarianceProfile.Fit(sessions, last.AddDays(1)));
	}
}
