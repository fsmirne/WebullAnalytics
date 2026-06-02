using WebullAnalytics.AI.Analysis;
using Xunit;

namespace WebullAnalytics.Tests.AI.Analysis;

public class DipOptionsOverlayTests
{
	private const double R = 0.043;
	private const double T1Day = 1.0 / 365.0;   // 1 day to expiry
	private const decimal Iv = 0.15m;

	[Fact]
	public void CallReturn_UpMove_IsPositive_FlatMove_LosesToTheta()
	{
		// Hold ~half the day (t0=1d → t1=0.5d). A meaningful up-move should profit; a flat move must lose to theta.
		var up = DipOptionsOverlay.CallReturnOnPremium(s0: 6000m, s1: 6030m, k: 6000m, t0: T1Day, t1: T1Day / 2, R, Iv, roundTripSpread: 0m);
		var flat = DipOptionsOverlay.CallReturnOnPremium(s0: 6000m, s1: 6000m, k: 6000m, t0: T1Day, t1: T1Day / 2, R, Iv, roundTripSpread: 0m);
		Assert.NotNull(up);
		Assert.NotNull(flat);
		Assert.True(up!.Value > 0m, $"up-move return {up} should be positive");
		Assert.True(flat!.Value < 0m, $"flat move return {flat} should be negative (theta decay)");
	}

	[Fact]
	public void CallReturn_SpreadReducesReturn()
	{
		var gross = DipOptionsOverlay.CallReturnOnPremium(6000m, 6030m, 6000m, T1Day, T1Day / 2, R, Iv, 0m)!.Value;
		var net = DipOptionsOverlay.CallReturnOnPremium(6000m, 6030m, 6000m, T1Day, T1Day / 2, R, Iv, 0.03m)!.Value;
		Assert.Equal(gross - 0.03m, net, precision: 6);
	}

	[Fact]
	public void Delta25Strike_IsOtm_AndLowerDeltaThanAtm()
	{
		var k25 = DipOptionsOverlay.Delta25Strike(6000m, T1Day, R, Iv);
		Assert.True(k25 > DipOptionsOverlay.AtmStrike(6000m), "0.25-delta call strike must be OTM (above spot)");
	}

	[Fact]
	public void ShortPutStrikeForDelta_IsOtm_BelowSpot()
	{
		var k30 = DipOptionsOverlay.ShortPutStrikeForDelta(6000m, T1Day, R, Iv, 0.30m);
		var k15 = DipOptionsOverlay.ShortPutStrikeForDelta(6000m, T1Day, R, Iv, 0.15m);
		Assert.True(k30 < 6000m, "0.30Δ put strike must be OTM (below spot)");
		Assert.True(k15 < k30, "0.15Δ put must be further OTM (lower strike) than 0.30Δ");
	}

	private static decimal? Iv15(DateTime _) => 15m;
	private static readonly DateTime Entry = new(2025, 6, 2, 9, 30, 0);
	private static IntradayBar Bar(int min, decimal close) => new(Entry.AddMinutes(min), close, close, close, close, 0);
	private static DipSignal AtmSignal => new(Entry, 6000m, 28m, 5990m, 5995m, -1m, null, null, 0m); // ATM strike → 6000, long 5975

	[Fact]
	public void PutSpread_SettleMath_FullCreditAboveShort_MaxLossBelowLong()
	{
		// Settle is driven by the LAST bar's close. Up close (6010 > short 6000) keeps credit; deep-down close
		// (5970 < long 5975) is max loss. No stop.
		var win = DipOptionsOverlay.RunPutSpreads(new[] { Bar(0, 6000m), Bar(385, 6010m) }, new[] { AtmSignal }, Iv15, R, 25m, 0m, 0m, 0m);
		var loss = DipOptionsOverlay.RunPutSpreads(new[] { Bar(0, 6000m), Bar(385, 5970m) }, new[] { AtmSignal }, Iv15, R, 25m, 0m, 0m, 0m);

		Assert.True(win.Single(x => x.ShortMode == "ATM").AvgPnl > 0m, "up close → keep credit");
		Assert.True(loss.Single(x => x.ShortMode == "ATM").AvgPnl < win.Single(x => x.ShortMode == "ATM").AvgPnl, "deep-down close must be worse");
	}

	[Fact]
	public void PutSpread_Stop_CutsTheTrade_WhenPathDipsThenRecovers()
	{
		// Path dips deep (5900, spread near max loss) mid-session, then recovers to a winning close (6010).
		// Held to settle → keeps credit (positive). With a 2× stop → cut on the dip (negative). So the stop must
		// produce a worse result here AND report a stop.
		var path = new[] { Bar(0, 6000m), Bar(120, 5900m), Bar(385, 6010m) };
		var noStop = DipOptionsOverlay.RunPutSpreads(path, new[] { AtmSignal }, Iv15, R, 25m, 0m, stopMultiple: 0m, skewPtsPerPct: 0m);
		var stopped = DipOptionsOverlay.RunPutSpreads(path, new[] { AtmSignal }, Iv15, R, 25m, 0m, stopMultiple: 2m, skewPtsPerPct: 0m);

		Assert.True(noStop.Single(x => x.ShortMode == "ATM").AvgPnl > 0m, "no-stop recovers to a winning close");
		Assert.Equal(1.0, stopped.Single(x => x.ShortMode == "ATM").StoppedRate);
		Assert.True(stopped.Single(x => x.ShortMode == "ATM").AvgPnl < 0m, "stopped on the dip → realized loss");
	}
}
