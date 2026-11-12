using WebullAnalytics.AI;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

/// <summary>Locks the implied-vol calibration on a synthetic market whose variance switches between a calm and a stressed regime every
/// 40 sessions, with VIX9D/VIX1D quoting the coming variance exactly. The trailing EWMA lags each switch by construction, so a correct
/// causal fit scales the forecast up just after a jump into the stressed regime and down just after the return to calm; with under
/// two years of history it leaves the EWMA alone.</summary>
public sealed class ImpliedVolForecastTests : IDisposable
{
	private const double Calm = 0.00004, Stressed = 0.00036;
	private const int Block = 40;
	private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ImpliedVolForecastTests_{Guid.NewGuid():N}");

	public ImpliedVolForecastTests() => Directory.CreateDirectory(_dir);
	public void Dispose() => Directory.Delete(_dir, recursive: true);

	private static double VarianceOf(int session) => session / Block % 2 == 0 ? Calm : Stressed;

	/// <summary>Weekday sessions from 2021-01-04: per-session gap and session moves with the regime's variance (fixed seed), and
	/// VIX9D/VIX1D closes quoting the next five sessions' / next session's variance.</summary>
	private static (List<DateTime> Days, Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> Bars) Market(int sessions)
	{
		var rng = new Random(7);
		var days = new List<DateTime>();
		for (var d = new DateTime(2021, 1, 4); days.Count < sessions; d = d.AddDays(1))
			if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
		var spy = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		var vix9d = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		var vix1d = new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>();
		var close = 400.0;
		for (var i = 0; i < sessions; i++)
		{
			var v = VarianceOf(i);
			var open = close * Math.Exp(Normal(rng) * Math.Sqrt(v * 0.2));
			var next = open * Math.Exp(Normal(rng) * Math.Sqrt(v * 0.8));
			spy[days[i]] = new(days[i], (decimal)open, (decimal)(Math.Max(open, next) * 1.0005), (decimal)(Math.Min(open, next) * 0.9995), (decimal)next, (decimal)next, null);
			close = next;
			var ahead5 = Enumerable.Range(i + 1, 5).Average(VarianceOf);
			vix9d[days[i]] = Vix(days[i], ahead5);
			vix1d[days[i]] = Vix(days[i], VarianceOf(i + 1));
		}
		return (days, new() { ["SYNTH"] = spy, ["VIX9D"] = vix9d, ["VIX1D"] = vix1d });
	}

	private static double Normal(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

	private static YahooOptionsClient.HistoricalBar Vix(DateTime day, double sessionVariance)
	{
		var points = (decimal)(Math.Sqrt(sessionVariance * 252) * 100);
		return new(day, points, points, points, points, points, null);
	}

	/// <summary>A price cache over the synthetic market seeded the way the on-disk store holds history (one CSV per ticker, full depth):
	/// a cold cache fetches only two years, which the fit correctly treats as too short.</summary>
	private HistoricalPriceCache Cache(Dictionary<string, Dictionary<DateTime, YahooOptionsClient.HistoricalBar>> bars, string ticker)
	{
		foreach (var (name, series) in bars)
			File.WriteAllLines(Path.Combine(_dir, $"{(name == "SYNTH" ? ticker : name)}.csv"), new[] { "date,open,high,low,close,adj_close,volume" }.Concat(series.OrderBy(kv => kv.Key).Select(kv => FormattableString.Invariant($"{kv.Key:yyyy-MM-dd},{kv.Value.Open},{kv.Value.High},{kv.Value.Low},{kv.Value.Close},{kv.Value.AdjClose},"))));
		return new(_dir, (t, from, to, _) => Task.FromResult(new Dictionary<DateTime, YahooOptionsClient.HistoricalBar>()), utcNow: () => new DateTime(2030, 1, 1));
	}

	[Fact]
	public async Task ScalesUpAfterAJumpIntoStressAndDownAfterTheReturnToCalm()
	{
		var (days, bars) = Market(1000);
		var cache = Cache(bars, "SYNTHUP");
		// Session 841 is the second session of a stressed block (840..879); session 881 the second of the calm block after it.
		var intoStress = await ForecastAsync(cache, "SYNTHUP", days[841]);
		var backToCalm = await ForecastAsync(cache, "SYNTHUP", days[881]);

		Assert.True(intoStress.ForwardScale > 1.5, $"forward scale into stress {intoStress.ForwardScale:F2}");
		Assert.True(intoStress.TodayScale > 1.5, $"today scale into stress {intoStress.TodayScale:F2}");
		Assert.True(backToCalm.ForwardScale < 0.7, $"forward scale back to calm {backToCalm.ForwardScale:F2}");
		Assert.True(backToCalm.TodayScale < 0.7, $"today scale back to calm {backToCalm.TodayScale:F2}");
	}

	[Fact]
	public async Task UnderTwoYearsOfHistoryLeavesTheEwmaAlone()
	{
		var (days, bars) = Market(400);
		var model = await ForecastAsync(Cache(bars, "SYNTHSHORT"), "SYNTHSHORT", days[^2]);
		Assert.Equal((1.0, 1.0), (model.TodayScale, model.ForwardScale));
	}

	private static async Task<RealizedVolModel> ForecastAsync(HistoricalPriceCache cache, string ticker, DateTime asOf) =>
		(await RealizedVolModel.LoadAsync(cache, ticker, asOf, impliedForecast: true, CancellationToken.None))!;
}
