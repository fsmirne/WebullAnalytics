using System.Collections.Concurrent;
using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI.Rules;

/// <summary>Layer-2 expiry-day session classification (campaign: gex_layers). Computed from the SHORT
/// leg's own-expiry GEX at the day's first management evaluation: a PIN session (call-dominated book,
/// gravity close to spot) historically compresses movement — friendly to holding a decaying short — while
/// an AMPLIFICATION session (put-dominated book) is where dealer hedging accelerates moves through the
/// strike. <see cref="ExpiryDayRegime.None"/> = no data or no clear read → the rule behaves exactly as
/// unmodulated, so a missing OI snapshot day can never change behavior.</summary>
internal enum ExpiryDayRegime { None, Pin, Amplification }

/// <summary>Per-(ticker, ET date) regime store, populated by the host (backtest runner / live watch) that
/// owns a quote source able to fetch the expiry-day chain — the management evaluation context itself only
/// carries position-leg quotes, so the rule cannot classify from what it sees. Absent entry → None.</summary>
internal sealed class ExpiryRegimeProvider
{
	private readonly ConcurrentDictionary<(string Ticker, DateTime Date), ExpiryDayRegime> _byDay = new();

	public ExpiryDayRegime Get(string ticker, DateTime date) =>
		_byDay.TryGetValue((ticker.ToUpperInvariant(), date.Date), out var r) ? r : ExpiryDayRegime.None;

	public bool Has(string ticker, DateTime date) => _byDay.ContainsKey((ticker.ToUpperInvariant(), date.Date));

	public void Set(string ticker, DateTime date, ExpiryDayRegime regime) =>
		_byDay[(ticker.ToUpperInvariant(), date.Date)] = regime;

	/// <summary>Pure classification from the expiry's GEX aggregate. Amplification wins ties: a book that
	/// is put-dominated is amplifying even if gravity happens to sit nearby.</summary>
	public static ExpiryDayRegime Classify(CandidateScorer.GexResult gex, decimal spot, ExpiryDayRegimeConfig cfg)
	{
		if (!cfg.Enabled || spot <= 0m || gex.GexGravity == null) return ExpiryDayRegime.None;
		if (gex.NetGexFraction <= cfg.AmpMaxNetGexFraction) return ExpiryDayRegime.Amplification;
		if (gex.NetGexFraction >= cfg.PinMinNetGexFraction && Math.Abs(gex.GexGravity.Value - spot) / spot <= cfg.PinMaxGravityDistancePct)
			return ExpiryDayRegime.Pin;
		return ExpiryDayRegime.None;
	}
}

/// <summary>Layer-2 knobs, nested under rules.closeBeforeShortExpiry.regime. Off by default; when off (or
/// unclassified) the parent rule is bit-identical to its unmodulated behavior.</summary>
internal sealed class ExpiryDayRegimeConfig
{
	[System.Text.Json.Serialization.JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;

	/// <summary>Net-GEX fraction at or below which the expiry session reads as AMPLIFICATION (put-dominated).
	/// Default 0 = any put-dominated book.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("ampMaxNetGexFraction")] public decimal AmpMaxNetGexFraction { get; set; } = 0m;

	/// <summary>Net-GEX fraction at or above which (with gravity near spot) the session reads as PIN. Default 0.2.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("pinMinNetGexFraction")] public decimal PinMinNetGexFraction { get; set; } = 0.2m;

	/// <summary>Max |gravity − spot| / spot for the PIN read. Default 0.005 (0.5%).</summary>
	[System.Text.Json.Serialization.JsonPropertyName("pinMaxGravityDistancePct")] public decimal PinMaxGravityDistancePct { get; set; } = 0.005m;

	/// <summary>AMPLIFICATION sessions: the profit gate fires at MinProfitPct × this factor — defend earlier,
	/// take the smaller win before the terrain can manufacture the tail. Default 0.5. 1.0 = no change.</summary>
	[System.Text.Json.Serialization.JsonPropertyName("ampMinProfitPctFactor")] public decimal AmpMinProfitPctFactor { get; set; } = 0.5m;

	/// <summary>PIN sessions: the PROFIT-gated close (never the emergencies) is deferred until this ET time —
	/// let pin-day decay run before banking. "HH:mm"; null/empty = no deferral. Default "15:00".</summary>
	[System.Text.Json.Serialization.JsonPropertyName("pinDeferProfitCloseUntilEt")] public string? PinDeferProfitCloseUntilEt { get; set; } = "15:00";
}

/// <summary>Shared host-side population: one probe fetch per (ticker, day) with a short leg expiring
/// today — the probe symbol's (root, expiry) makes both the live vendor and the backtest chain expansion
/// return the full expiry-day chain with OI, which is exactly what <see cref="CandidateScorer.ComputeGex"/>
/// needs. Called by the backtest runner's daily step and the live watch tick; scan paths skip it (regime
/// stays None → unmodulated).</summary>
internal static class ExpiryRegimeHost
{
	public static async Task PopulateAsync(ExpiryRegimeProvider provider, ExpiryDayRegimeConfig cfg,
		IEnumerable<OpenPosition> openPositions, IQuoteSource quotes, DateTime asOf, CancellationToken cancellation)
	{
		if (!cfg.Enabled) return;
		foreach (var group in openPositions.GroupBy(p => p.Ticker, StringComparer.OrdinalIgnoreCase))
		{
			if (provider.Has(group.Key, asOf.Date)) continue;
			var shortToday = group.SelectMany(p => p.Legs).FirstOrDefault(l => l.Side == Side.Sell && l.CallPut != null && l.Expiry.HasValue && l.Expiry.Value.Date == asOf.Date);
			if (shortToday == null) continue;
			var snap = await quotes.GetQuotesAsync(asOf,
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { shortToday.Symbol },
				new HashSet<string>(StringComparer.OrdinalIgnoreCase) { group.Key }, cancellation);
			if (!snap.Underlyings.TryGetValue(group.Key, out var spot) || spot <= 0m) continue;
			var gex = CandidateScorer.ComputeGex(group.Key, asOf.Date, spot, asOf, snap.Options);
			provider.Set(group.Key, asOf.Date, ExpiryRegimeProvider.Classify(gex, spot, cfg));
		}
	}
}
