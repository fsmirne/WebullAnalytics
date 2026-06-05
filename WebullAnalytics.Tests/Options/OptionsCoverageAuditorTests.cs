using System.Globalization;
using WebullAnalytics.Options;
using Xunit;

namespace WebullAnalytics.Tests.Options;

/// <summary>Unit tests for the pure coverage-analysis core of `wa options audit` (no disk I/O). Each test
/// feeds a hand-built expiry → (session → contract count) map and asserts which gaps are flagged.</summary>
public class OptionsCoverageAuditorTests
{
	private static DateTime D(string iso) => DateTime.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

	/// <summary>Coverage for one expiry: the given density on each listed session.</summary>
	private static SortedDictionary<DateTime, int> Cov(int density, params string[] sessions)
	{
		var m = new SortedDictionary<DateTime, int>();
		foreach (var s in sessions) m[D(s)] = density;
		return m;
	}

	// windowStart past the frontier disables the missing-expiry scan, isolating the other checks.
	private static readonly DateTime NoMissingScan = D("2030-01-01");

	[Fact]
	public void CleanCoverage_NoGapsFlagged()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-30")] = Cov(50, "2026-06-01", "2026-06-02", "2026-06-03", "2026-06-04"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, todayEt: D("2026-06-04"), expectedFrontier: D("2026-06-04"), lookback: 70);

		Assert.True(r.Clean);
		Assert.Equal(1, r.LiveCount);
		Assert.Equal(0, r.ExpiredCount);
		Assert.Empty(r.FrontierLag);
		Assert.Empty(r.InteriorGaps);
		Assert.Empty(r.MissingExpiries);
	}

	[Fact]
	public void FrontierLag_FlaggedWhenLiveExpiryBehindLastClosedSession()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-30")] = Cov(50, "2026-06-01", "2026-06-02"), // last 06-02, behind frontier 06-04
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		Assert.False(r.Clean);
		var lag = Assert.Single(r.FrontierLag);
		Assert.Equal(D("2026-06-30"), lag.Expiry);
		Assert.Equal(D("2026-06-02"), lag.Last);
	}

	[Fact]
	public void FrontierCurrent_NotFlagged()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-30")] = Cov(50, "2026-06-03", "2026-06-04"), // reaches frontier
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		Assert.Empty(r.FrontierLag);
	}

	[Fact]
	public void MissingExpiry_FlagsTradingDays_SkipsWeekends()
	{
		// Folders only on Thu 06-04 and Mon 06-08; Fri 06-05 has none (missing); 06-06/07 are a weekend.
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-04")] = Cov(50, "2026-06-04"),
			[D("2026-06-08")] = Cov(50, "2026-06-08"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, windowStart: D("2026-06-04"), todayEt: D("2026-06-09"), expectedFrontier: D("2026-06-08"), lookback: 70);

		Assert.Equal(new[] { D("2026-06-05") }, r.MissingExpiries);
	}

	[Fact]
	public void InteriorGap_FlaggedBetweenDenseSessions()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			// 06-02 is an open session missing between two dense (50) sessions.
			[D("2026-06-30")] = Cov(50, "2026-06-01", "2026-06-03", "2026-06-04"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		var gap = Assert.Single(r.InteriorGaps);
		Assert.Equal(D("2026-06-30"), gap.Expiry);
		Assert.Equal(new[] { D("2026-06-02") }, gap.Days);
		Assert.Equal(1, r.InteriorGapTotal);
	}

	[Fact]
	public void InteriorGap_NotFlaggedWhenNeighborsThin()
	{
		// Same hole, but neighbors carry only 5 contracts (< MinNeighborDensity) — illiquidity, not a defect.
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-30")] = Cov(5, "2026-06-01", "2026-06-03", "2026-06-04"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		Assert.Empty(r.InteriorGaps);
		Assert.True(r.Clean);
	}

	[Fact]
	public void InteriorGap_RespectsLookbackWindow()
	{
		// Hole at 06-23 between dense sessions; rest contiguous through expiry. Expired so no frontier concern.
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-30")] = Cov(50, "2026-06-22", "2026-06-24", "2026-06-25", "2026-06-26", "2026-06-29", "2026-06-30"),
		};

		// lookback 5 → gapFloor 06-25, so the 06-23 hole is out of window → not flagged.
		var tight = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, todayEt: D("2026-07-15"), expectedFrontier: D("2026-07-14"), lookback: 5);
		Assert.Empty(tight.InteriorGaps);

		// lookback 10 → gapFloor 06-20, so 06-23 is in window → flagged.
		var wide = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-07-15"), D("2026-07-14"), lookback: 10);
		var gap = Assert.Single(wide.InteriorGaps);
		Assert.Equal(new[] { D("2026-06-23") }, gap.Days);
	}

	[Fact]
	public void IncompleteExpired_FlaggedWhenLastBarWellBeforeExpiry()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			// Expiry 05-29, capture stops 05-15 (> 7 sessions short) → incomplete.
			[D("2026-05-29")] = Cov(50, "2026-05-13", "2026-05-14", "2026-05-15"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, todayEt: D("2026-06-04"), expectedFrontier: D("2026-06-04"), lookback: 70);

		var inc = Assert.Single(r.IncompleteExpired);
		Assert.Equal(D("2026-05-29"), inc.Expiry);
		Assert.Equal(1, r.ExpiredCount);
	}

	[Fact]
	public void IncompleteExpired_NotFlaggedWithinTolerance()
	{
		// Illiquid contract stops a few sessions early but within the 7-day tolerance → complete.
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-05-29")] = Cov(50, "2026-05-26"),
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		Assert.Empty(r.IncompleteExpired);
	}

	[Fact]
	public void Counts_SplitLiveAndExpiredByToday()
	{
		var expiries = new Dictionary<DateTime, SortedDictionary<DateTime, int>>
		{
			[D("2026-06-12")] = Cov(50, "2026-06-03", "2026-06-04"), // live, current
			[D("2026-06-30")] = Cov(50, "2026-06-03", "2026-06-04"), // live, current
			[D("2026-05-29")] = Cov(50, "2026-05-28", "2026-05-29"), // expired, complete
		};
		var r = OptionsCoverageAuditor.Audit(expiries, NoMissingScan, D("2026-06-04"), D("2026-06-04"), 70);

		Assert.Equal(2, r.LiveCount);
		Assert.Equal(1, r.ExpiredCount);
		Assert.True(r.Clean);
	}
}
