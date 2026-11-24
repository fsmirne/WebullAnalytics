using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WebullAnalytics.Api;

/// <summary>Schwab Trader market-data option-chain client. One GET to <c>/marketdata/v1/chains</c> returns the
/// whole chain for an underlying with real NBBO (bid/ask), open interest, volume, IV, and greeks — the data
/// Webull's snapshot path leaves null for non-front expiries. Maps Schwab's nested callExpDateMap/putExpDateMap
/// into the internal <see cref="OptionContractQuote"/> model with the same compact OCC symbol the rest of the
/// pipeline uses.</summary>
internal static class SchwabOptionsClient
{
	private const string Host = "api.schwabapi.com";
	private const string ChainsPath = "/marketdata/v1/chains";
	private const string ChainsUrl = "https://" + Host + ChainsPath;

	/// <summary>Most recent extended-hours (top-level <c>underlyingPrice</c> / <c>underlying.mark</c>) price seen per
	/// Schwab underlying symbol (stripped of any leading <c>$</c>), populated as a zero-cost side effect of every
	/// <see cref="ParseChain"/> call so <c>--extended</c> can project at the after-hours / premarket equity mark while
	/// <c>Spot</c> stays at <c>underlying.last</c> (the regular-session close the option book is priced against).</summary>
	private static readonly ConcurrentDictionary<string, decimal> LastExtendedSpots = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Returns the most recent extended-hours spot captured for <paramref name="root"/> by a Schwab chain
	/// fetch in this process, or null when none has been recorded.</summary>
	internal static decimal? TryGetExtendedSpot(string root) =>
		LastExtendedSpots.TryGetValue(ToSchwabUnderlying(root).TrimStart('$'), out var s) && s > 0m ? s : null;

	/// <summary>Internal ticker root → Schwab underlying symbol. Index options use a '$' prefix; requesting
	/// <c>$SPX</c> returns BOTH the SPX (AM monthly) and SPXW (PM weekly) roots, so a single call covers the
	/// SPXW research vehicle. Equities/ETFs (SPY) pass through unchanged.</summary>
	private static readonly Dictionary<string, string> UnderlyingMap = new(StringComparer.OrdinalIgnoreCase)
	{
		["SPX"] = "$SPX",
		["SPXW"] = "$SPX",
		["XSP"] = "$XSP",
		["VIX"] = "$VIX",
		["NDX"] = "$NDX",
		["RUT"] = "$RUT",
		["DJX"] = "$DJX",
	};

	public static string ToSchwabUnderlying(string root) =>
		UnderlyingMap.TryGetValue(root, out var s) ? s : root.ToUpperInvariant();

	/// <summary>Fetches the full chain for <paramref name="root"/> between the given expiry dates (inclusive).
	/// Returns the underlying spot and every quoted contract. An empty result (e.g. an unsupported index symbol)
	/// comes back as <c>(null, empty)</c> rather than throwing, so the capture loop treats it as a soft miss. So does a fetch
	/// any of whose expiry windows failed: the chain is whole or absent, never missing a span of expiries.</summary>
	/// <param name="strikeCount">When > 0, asks Schwab to return only this many strikes around the
	/// at-the-money price (per expiration) instead of the full <c>range=ALL</c> ladder. A caller that only
	/// needs near-money strikes (e.g. <c>analyze gex</c>) sets this so the common case is a single small
	/// request; 0 keeps the full ladder (what the scraper's backtest snapshot wants). Either way the body
	/// cap is enforced by the adaptive date-range split below, so no caller can 502.</param>
	public static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)> FetchChainAsync(
		string accessToken, string root, DateOnly fromExpiry, DateOnly toExpiry, CancellationToken ct, int strikeCount = 0)
	{
		var symbol = ToSchwabUnderlying(root);
		var maxSpan = FittingSpanDays.TryGetValue((symbol, strikeCount), out var span) ? span : int.MaxValue;
		var windows = PlanWindows(fromExpiry, toExpiry, maxSpan);
		var parts = await Task.WhenAll(windows.Select(w => FetchChainRangeAsync(accessToken, symbol, w.From, w.To, strikeCount, ct)));
		return Merge(parts) ?? (null, Array.Empty<OptionContractQuote>());
	}

	/// <summary>The widest expiry window (in days) known to fit under Schwab's body cap, per underlying and strike bound: the half of the last window that overflowed it. A dense
	/// index chain overflows at range=ALL over a multi-week window, and the overflow is only reported after Schwab has built the response (a rejected 30-day $SPX request took
	/// 10.5 s, more than the two requests that then succeeded together), so once a window is known too wide the next fetch starts from windows that fit instead of paying for it
	/// again. Only ever narrows, and lasts the process: density shifts slowly, and a narrower window costs one more parallel request, not a failure.</summary>
	private static readonly ConcurrentDictionary<(string Symbol, int StrikeCount), int> FittingSpanDays = new();

	/// <summary>Splits [<paramref name="from"/>, <paramref name="to"/>] into consecutive windows of at most <paramref name="maxSpanDays"/> days each.</summary>
	internal static IReadOnlyList<(DateOnly From, DateOnly To)> PlanWindows(DateOnly from, DateOnly to, int maxSpanDays)
	{
		var span = Math.Max(1, maxSpanDays);
		if (to <= from || to.DayNumber - from.DayNumber < span) return [(from, to)];
		var windows = new List<(DateOnly, DateOnly)>();
		for (var start = from; start <= to; start = start.AddDays(span))
		{
			windows.Add((start, to.DayNumber - start.DayNumber < span ? to : start.AddDays(span - 1)));
		}
		return windows;
	}

	/// <summary>The windows' chains as one: contracts keyed by symbol (a later window wins a repeat), and the first spot any of them carried.
	/// Null when any window failed (null): merging the rest would hand the caller a chain missing those expiries as if it were whole.</summary>
	internal static (decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)? Merge(IEnumerable<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)?> parts)
	{
		var merged = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
		decimal? spot = null;
		foreach (var part in parts)
		{
			if (part is not { } p) return null;
			spot ??= p.Spot;
			foreach (var q in p.Quotes) merged[q.ContractSymbol] = q;
		}
		return (spot, merged.Values.ToList());
	}

	/// <summary>Fetches [<paramref name="from"/>, <paramref name="to"/>] in one request and, when Schwab
	/// rejects the response body as too large (HTTP 502 <c>protocol.http.TooBigBody</c> — dense index roots
	/// like $SPX over a multi-day window with <c>range=ALL</c>), halves the expiry window, fetches both halves
	/// at once and merges them, remembering the narrower span (<see cref="FittingSpanDays"/>) for the next fetch.
	/// Self-tunes to chain density: sparse equities resolve in one call, a 45-DTE $SPX
	/// request splits until each chunk fits — full strike coverage preserved, no per-ticker tuning, and no
	/// caller (analyze gex or the scraper, at any <c>--max-dte</c>) can overflow. Null when a request failed.</summary>
	private static async Task<(decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)?> FetchChainRangeAsync(
		string accessToken, string symbol, DateOnly from, DateOnly to, int strikeCount, CancellationToken ct)
	{
		var (result, tooBig) = await FetchChainOnceAsync(accessToken, symbol, from, to, strikeCount, ct);
		if (!tooBig) return result;

		if (from >= to)
		{
			// A single expiry day still overflows at range=ALL — last resort: bound strikes around ATM so we
			// return the near-money chain rather than nothing. (Not expected in practice: 0DTE range=ALL fits.)
			if (strikeCount is 0 or > 250)
			{
				var (bounded, stillTooBig) = await FetchChainOnceAsync(accessToken, symbol, from, to, 250, ct);
				if (!stillTooBig) return bounded;
			}
			Console.WriteLine($"Schwab: chain for {symbol} on {from:yyyy-MM-dd} exceeds the body cap even bounded; skipping that day.");
			return (null, Array.Empty<OptionContractQuote>());
		}

		var spanDays = to.DayNumber - from.DayNumber + 1;
		FittingSpanDays.AddOrUpdate((symbol, strikeCount), spanDays / 2, (_, known) => Math.Min(known, spanDays / 2));
		var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
		var halves = await Task.WhenAll(FetchChainRangeAsync(accessToken, symbol, from, mid, strikeCount, ct), FetchChainRangeAsync(accessToken, symbol, mid.AddDays(1), to, strikeCount, ct));
		return Merge(halves);
	}

	/// <summary>One chains GET. Returns <c>TooBig=true</c> on the 502 body-overflow so the caller can split the window;
	/// other non-success statuses are logged and returned as a failure (null result).</summary>
	private static async Task<((decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes)? Result, bool TooBig)> FetchChainOnceAsync(
		string accessToken, string symbol, DateOnly from, DateOnly to, int strikeCount, CancellationToken ct)
	{
		var url = $"{ChainsUrl}?symbol={Uri.EscapeDataString(symbol)}&contractType=ALL&strategy=SINGLE&range=ALL"
			+ $"&includeUnderlyingQuote=true&fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}"
			+ (strikeCount > 0 ? $"&strikeCount={strikeCount}" : "");

		var (statusCode, body) = await SendWithTransientRetryAsync(url, $"{symbol} {from:yyyy-MM-dd}..{to:yyyy-MM-dd}", accessToken, ct);
		if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			throw new SchwabAuthException($"chains request unauthorized (HTTP {(int)statusCode}) — token rejected.");
		if (!IsSuccess(statusCode))
		{
			var tooBig = (int)statusCode == 502 && body.Contains("TooBigBody", StringComparison.OrdinalIgnoreCase);
			if (!tooBig)
				Console.WriteLine($"Schwab: chains returned {(int)statusCode} for {symbol}: {Truncate(body, 200)}");
			return (null, tooBig);
		}

		return (ParseChain(body, symbol), false);
	}

	private static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and <= 299;

	/// <summary>Issues the chains GET with a bounded retry on transport-level transient failures — SSL handshake
	/// aborts and mid-request connection resets (<c>HttpRequestException</c>/<c>IOException</c>, typically an inner
	/// <c>SocketException</c> "forcibly closed by the remote host") that show up as intermittent single-tick failures
	/// in the live watch loop. Sends through the pooled <see cref="SchwabHttp.Client"/> (see its comment: churning a
	/// fresh client per call is what provokes the RST in the first place); a fresh request message is used per attempt.
	/// Every attempt waits its turn at <see cref="RateGate.Schwab"/>, shared by every local process, and a 429 — Schwab's own sporadic
	/// throttling, which arrives well under any rate the gate holds (see its comment) — is retried after its Retry-After, a bounded number of times. Other failures (auth, 502 TooBig, timeouts)
	/// propagate to the caller unchanged. <paramref name="label"/> names the request (symbol and expiry window) in the 429 line, with Schwab's body and headers (<see cref="DescribeThrottle"/>),
	/// so a throttled request can be told apart from the rest of a tick's fetches.</summary>
	private static async Task<(HttpStatusCode Status, string Body)> SendWithTransientRetryAsync(string url, string label, string accessToken, CancellationToken ct)
	{
		const int maxAttempts = 3;
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				await RateGate.Schwab.WaitAsync(Host, "", ChainsPath, ct);
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

				using var response = await SchwabHttp.Client.SendAsync(request, ct);
				if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
				{
					var wait = RateGate.RetryAfter(response, RateLimitWait, MaxRateLimitWait);
					var reason = DescribeThrottle(response, await response.Content.ReadAsStringAsync(ct));
					Console.WriteLine($"Schwab: chains HTTP 429 for {label} ({reason}); retry {attempt} of {maxAttempts - 1} in {wait.TotalSeconds:F1}s.");
					await Task.Delay(wait, ct);
					continue;
				}
				var body = await response.Content.ReadAsStringAsync(ct);
				return (response.StatusCode, body);
			}
			catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < maxAttempts && !ct.IsCancellationRequested)
			{
				Console.WriteLine($"Schwab: chains transport error (attempt {attempt}/{maxAttempts}), retrying: {ex.Message}");
				await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
			}
		}
	}

	/// <summary>A 429's reason on one line: its body with whitespace collapsed (first 200 characters), or "empty body", then every response and content header.
	/// Schwab's chains 429 arrives with a whitespace-only body (2026-11-24 09:44 ET, $SPX 0DTE window), so whatever names the throttle — a limit, a gateway, a
	/// correlation id Schwab support can trace — can only be in the headers.</summary>
	internal static string DescribeThrottle(HttpResponseMessage response, string body)
	{
		var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		var headers = response.Headers.Concat(response.Content.Headers).Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
		return $"{(text.Length == 0 ? "empty body" : Truncate(text, 200))}; headers: {string.Join("; ", headers)}";
	}

	/// <summary>The wait after a 429 that sends no Retry-After, and the longest one honored: a read retried later than this is stale for its tick.</summary>
	private static readonly TimeSpan RateLimitWait = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan MaxRateLimitWait = TimeSpan.FromSeconds(5);

	internal static (decimal? Spot, IReadOnlyList<OptionContractQuote> Quotes) ParseChain(string json, string symbol, bool? isRth = null, DateTime? nowEtOverride = null)
	{
		var quotes = new List<OptionContractQuote>();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// Schwab signals an empty/failed lookup with status SUCCESS but null maps, or status FAILED.
		var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
		if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
		{
			Console.WriteLine($"Schwab: chains status FAILED for {symbol} (symbol may be unsupported).");
			return (null, quotes);
		}

		var hasUnderlying = root.TryGetProperty("underlying", out var u) && u.ValueKind == JsonValueKind.Object;
		var topUnderlyingPrice = PositiveOrNull(GetDecimal(root, "underlyingPrice"));
		var regularLast = hasUnderlying ? PositiveOrNull(GetDecimal(u, "last")) : null;
		var extSpot = topUnderlyingPrice ?? (hasUnderlying ? PositiveOrNull(GetDecimal(u, "mark")) : null);

		foreach (var mapName in new[] { "callExpDateMap", "putExpDateMap" })
		{
			if (!root.TryGetProperty(mapName, out var expMap) || expMap.ValueKind != JsonValueKind.Object) continue;
			foreach (var expEntry in expMap.EnumerateObject())          // "YYYY-MM-DD:DTE"
			foreach (var strikeEntry in expEntry.Value.EnumerateObject()) // "500.0"
			foreach (var contract in strikeEntry.Value.EnumerateArray())
			{
				var rawSymbol = contract.TryGetProperty("symbol", out var s) ? s.GetString() : null;
				if (string.IsNullOrWhiteSpace(rawSymbol)) continue;

				quotes.Add(new OptionContractQuote(
					ContractSymbol: NormalizeSymbol(rawSymbol),
					LastPrice: PositiveOrNull(GetDecimal(contract, "last")),
					Bid: PositiveOrNull(GetDecimal(contract, "bid")),
					Ask: PositiveOrNull(GetDecimal(contract, "ask")),
					Change: GetDecimal(contract, "netChange"),
					PercentChange: GetDecimal(contract, "percentChange"),
					Volume: GetLong(contract, "totalVolume"),
					OpenInterest: GetLong(contract, "openInterest"),
					// Schwab quotes volatility as an annualized percent (e.g. 23.45); the model stores a fraction.
					// -999.0 is Schwab's "no value" sentinel.
					ImpliedVolatility: ToFraction(GetDecimal(contract, "volatility")),
					BidSize: GetLong(contract, "bidSize"),
					AskSize: GetLong(contract, "askSize"),
					// Schwab stamps each contract with quoteTimeInLong (epoch ms); feeds the live staleness guard.
					// Fall back to tradeTimeInLong when the quote time is absent.
					QuoteTime: EpochMsToOffset(GetLong(contract, "quoteTimeInLong") ?? GetLong(contract, "tradeTimeInLong"))
				));
			}
		}

		// During RTH (09:30–close ET), keep top-level underlyingPrice unchanged so live scan/watch/scraper loops
		// see the exact same live underlying mark as before. Outside RTH, inspect the option book itself to detect
		// whether the option quotes are live (e.g. premarket GTH / OPRA quotes already pricing the extended-hours
		// underlying level) or frozen at the regular-session 16:00 close:
		//   1. For cash indexes ($SPX / $XSP), Schwab freezes top-level underlyingPrice and underlying.last at the
		//      prior 16:00 close until 09:30 ET even while SPXW/XSP option quotes trade live in GTH; when put-call
		//      parity on the two-sided book diverges from the reported close, use the parity-implied spot.
		//   2. For equities/ETFs, when top-level underlyingPrice has moved away from underlying.last (or when only
		//      one side is present in a partial chain), check whether the option book tracks the extended-hours spot
		//      (via put-call parity when both calls and puts are present, else via quote timestamps after the 07:00 ET
		//      OPRA premarket start). If the option book is live, pair it with topUnderlyingPrice; if frozen at the
		//      16:00 close, pair it with regularLast and cache extSpot in LastExtendedSpots for --extended.
		var nowEt = nowEtOverride ?? MarketTime.NowEt();
		var inRth = isRth ?? IsRegularTradingHours(nowEt);
		decimal? spot = ResolveChainSpot(symbol, topUnderlyingPrice, regularLast, extSpot, quotes, inRth, nowEt);
		if ((extSpot ?? spot) is decimal recordedExt && recordedExt > 0m)
			LastExtendedSpots[symbol.TrimStart('$')] = recordedExt;

		// Once the chain is parsed, record whether it carries a live pre-open book (07:00+ ET on a trading day)
		// so direct callers of SchwabOptionsClient.FetchChainAsync (analyze gex, analyze cascade) get the same
		// 09:30 ObservationInstant anchor that LiveQuoteSource sets. Skip when isRth was explicitly injected (unit tests).
		if (!isRth.HasValue && !nowEtOverride.HasValue)
		{
			Pricing.OptionMath.LiveQuotesStruckAt = HasLivePremarketQuoteStamp(quotes, nowEt)
				? MarketTime.EtToInstant(nowEt)
				: null;
		}

		return (spot, quotes);
	}

	private static readonly TimeSpan PremarketOptionQuoteStart = new(7, 0, 0);
	private const decimal FrozenBookParityTolerance = 0.0025m;

	private static decimal? ResolveChainSpot(
		string symbol,
		decimal? topUnderlyingPrice,
		decimal? regularLast,
		decimal? extSpot,
		IReadOnlyList<OptionContractQuote> quotes,
		bool inRth,
		DateTime nowEt)
	{
		if (inRth)
			return topUnderlyingPrice ?? regularLast;

		var baselineSpot = regularLast ?? topUnderlyingPrice;
		if (quotes.Count == 0)
			return baselineSpot;

		var quoteDict = new Dictionary<string, OptionContractQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var q in quotes) quoteDict[q.ContractSymbol] = q;

		var cleanRoot = symbol.TrimStart('$');
		var parityTicker = string.Equals(cleanRoot, "SPX", StringComparison.OrdinalIgnoreCase) ? "SPXW" : cleanRoot;
		var parity = AI.PremarketSpotOverride.DeriveSpotFromParity(parityTicker, quoteDict, Pricing.OptionMath.RiskFreeRate, nowEt, allowLastPrice: false);

		// Cash indexes ($SPX / $XSP): Schwab's underlyingPrice & underlying.last both sit at the prior 16:00 close
		// until 09:30 ET, while SPXW/XSP option quotes update live during GTH. If parity on two-sided quotes has
		// moved more than 25 bp away from the reported close and the quotes are stamped today after 07:00 ET, the
		// option book is live at the parity-implied index level.
		if (AI.Sources.WebullIntradayBars.SpxFamilyTickers.Contains(parityTicker))
		{
			if (parity is { Spot: > 0m } idxParity && baselineSpot is > 0m
				&& Math.Abs(idxParity.Spot - baselineSpot.Value) / baselineSpot.Value > FrozenBookParityTolerance
				&& HasLivePremarketQuoteStamp(quotes, nowEt))
			{
				return Math.Round(idxParity.Spot, 2);
			}
			return baselineSpot;
		}

		// Equities/ETFs: if both extended spot (topUnderlyingPrice) and regular close (regularLast) are present
		// and differ, decide which spot the option book is actually priced against.
		if (extSpot is > 0m && regularLast is > 0m && extSpot.Value != regularLast.Value)
		{
			if (parity is { Spot: > 0m } eqParity)
			{
				var distExt = Math.Abs(eqParity.Spot - extSpot.Value);
				var distLast = Math.Abs(eqParity.Spot - regularLast.Value);
				return distExt < distLast ? extSpot : regularLast;
			}
			// Partial chain (e.g. calls only or single-leg fetch where parity has no same-strike pair): fall back
			// to whether the two-sided quotes carry a live premarket quote timestamp (after 07:00 ET today).
			// OPRA's 06:30 ET morning refresh re-stamps yesterday's closing quotes, whereas live premarket market-maker
			// quotes stream after 07:00 ET.
			if (HasLivePremarketQuoteStamp(quotes, nowEt))
				return extSpot;
		}

		return baselineSpot;
	}

	private static bool HasLivePremarketQuoteStamp(IReadOnlyList<OptionContractQuote> quotes, DateTime nowEt)
	{
		if (!MarketCalendar.IsOpen(nowEt.Date) || nowEt.TimeOfDay < PremarketOptionQuoteStart || nowEt.TimeOfDay >= Pricing.OptionMath.MarketOpen)
			return false;
		foreach (var q in quotes)
		{
			if (q.QuoteTime is not { } qt || q.Bid is not > 0m || q.Ask is not > 0m) continue;
			var qtEt = MarketTime.ToEt(qt);
			if (qtEt.Date == nowEt.Date && qtEt.TimeOfDay >= PremarketOptionQuoteStart)
				return true;
		}
		return false;
	}

	/// <summary>Schwab pads the OCC root to 6 chars (e.g. <c>"SPY   260620C00500000"</c>); strip the spaces to
	/// get the compact OCC symbol (<c>"SPY260620C00500000"</c>) the rest of the pipeline parses.</summary>
	internal static string NormalizeSymbol(string schwabSymbol) => schwabSymbol.Replace(" ", "");

	internal static bool IsRegularTradingHours(DateTime nowEt) =>
		MarketCalendar.IsOpen(nowEt.Date)
		&& nowEt.TimeOfDay >= Pricing.OptionMath.MarketOpen
		&& nowEt.TimeOfDay < MarketCalendar.CloseTimeEt(nowEt.Date);

	private static decimal? ToFraction(decimal? pct) => pct is { } v && v >= 0m ? v / 100m : null;

	private static decimal? PositiveOrNull(decimal? v) => v is { } d && d > 0m ? d : null;

	// Epoch milliseconds -> DateTimeOffset (UTC). 0/negative (Schwab's "no value") -> null.
	private static DateTimeOffset? EpochMsToOffset(long? epochMs) => epochMs is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

	private static decimal? GetDecimal(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetDecimal(out var d) && d > -900m ? d : null,
			JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null,
			_ => null,
		};
	}

	private static long? GetLong(JsonElement item, string prop)
	{
		if (!item.TryGetProperty(prop, out var el)) return null;
		return el.ValueKind switch
		{
			JsonValueKind.Number => el.TryGetInt64(out var l) ? l : null,
			JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : null,
			_ => null,
		};
	}

	private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
