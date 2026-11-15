using WebullAnalytics.AI.Backtest;
using WebullAnalytics.AI.Replay;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// One past session's cascade readings, replayed causally from stored data: the day's captured chain (data/oi), its minute tape
/// (data/intraday) and the quote store. <see cref="ReadAsync"/> scores the regime at an instant exactly as `analyze cascade --date
/// D --time T` renders it — the panel's default scope, only the bars that had closed by then, the front expiry priced from the
/// minute NBBO at that instant. Shared by `analyze cascade-study` and the cascade opener's backtest, so both see the same reading.
///
/// A session owns its price and SMILE caches (neither is thread-safe; both read their on-disk caches without fetching for past
/// dates), so sessions may be replayed in parallel but one session's reads must not be.
///
/// Every read prices on the session's own day: its trading-time clock calibration and, when given, its risk-free rate (the prior
/// ^IRX close, as the backtest uses for that day). Both are pinned for the read's own async flow, so a session replayed beside a
/// backtest's day loop — which re-sets the process-wide day and rate every simulated day — reads the same whenever it runs.
/// </summary>
internal sealed class CascadeReplaySession
{
	private readonly string _ticker;
	private readonly DateTime _date;
	private readonly Dictionary<string, OptionContractQuote> _snapshot;
	private readonly CascadeReplayCache _cache;
	private readonly HistoricalPriceCache _priceCache = new();
	private readonly SmileIndexCache _smileCache = new(offline: true);
	private readonly double? _riskFreeRate;

	private CascadeReplaySession(string ticker, DateTime date, Dictionary<string, OptionContractQuote> snapshot, IReadOnlyList<MinuteBar> dayBars, CascadeReplayCache cache, double? riskFreeRate)
	{
		_riskFreeRate = riskFreeRate;
		_ticker = ticker;
		_date = date;
		_snapshot = snapshot;
		DayBars = dayBars;
		_cache = cache;
	}

	/// <summary>The session's regular-hours minute bars (START-of-bar labels).</summary>
	public IReadOnlyList<MinuteBar> DayBars { get; }

	/// <summary>Regular-session close of the date (13:00 on a half-day).</summary>
	public TimeSpan Close => MarketCalendar.CloseTimeEt(_date);

	/// <summary>Loads the session's stored inputs; a null session carries the reason it cannot be replayed.</summary>
	/// <param name="riskFreeRate">The session's rate; null prices on the process-wide rate.</param>
	public static async Task<(CascadeReplaySession? Session, string? Skip)> OpenAsync(string ticker, DateTime date, CascadeReplayCache cache, double? riskFreeRate, CancellationToken ct)
	{
		var oiPath = Program.ResolvePath($"data/oi/{ticker}/{date:yyyy-MM-dd}.jsonl");
		if (!File.Exists(oiPath)) return (null, "no data/oi snapshot");
		var snapshot = AnalyzeGexCommand.LoadOiSnapshot(oiPath);
		if (snapshot.Quotes.Count == 0) return (null, "empty data/oi snapshot");

		var dayBars = await CascadeScreener.LoadSessionRthBarsAsync(ticker, date + MarketCalendar.CloseTimeEt(date), intradayCache: null, includeCurrentBar: false, ct);
		if (dayBars.Count == 0) return (null, "no data/intraday tape");
		return (new CascadeReplaySession(ticker, date.Date, snapshot.Quotes, dayBars, cache, riskFreeRate), null);
	}

	/// <summary>The reading at <paramref name="timeEt"/> of the session, with the chain it was read from; null when no bar had closed by
	/// then, the instant is at or after the close, the chain carries no open interest, or no strike falls in scope.</summary>
	public async Task<CascadeLiveRead?> ReadAsync(TimeSpan timeEt, CancellationToken ct)
	{
		if (timeEt >= Close) return null;
		using var day = Pricing.MarketClock.PinDay(DateOnly.FromDateTime(_date));
		using var rate = _riskFreeRate is double r ? Pricing.OptionMath.PinRiskFreeRate(r) : null;
		var asOfEt = _date + timeEt;
		// START-of-bar labels: the bars that closed by the instant are the reading's, and the last one's close is the price AT it.
		var rthBars = DayBars.Where(b => MarketTime.ToEt(b.Timestamp).TimeOfDay < timeEt).ToList();
		if (rthBars.Count == 0) return null;
		var spot = rthBars[^1].Close;

		var quotes = CascadeScreener.BuildCausalHistoricalChain(_ticker, asOfEt, _snapshot, forceExante: false, out _, _cache);
		if (AnalyzeGexCommand.IsOiBlackout(quotes, _ticker, _date)) return null;
		var read = await CascadeScreener.ReadChainAsync(_ticker, spot, asOfEt, quotes, CascadeScreener.DefaultDte, CascadeScreener.DefaultStrikeRangePct / 100m, CascadeScreener.DefaultMaxStrikes, CascadeScreener.DefaultTriggerScore, rthBars, null, _priceCache, _smileCache, "schwab", includeCurrentBar: false, ct, _cache);
		return read == null ? null : new CascadeLiveRead(read.Value.Reading, read.Value.ExpiryCount, spot, quotes);
	}
}
