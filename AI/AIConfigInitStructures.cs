using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.AI;

/// <summary>Live chain read once by <c>wa ai config init &lt;TICKER&gt;</c>: spot, the listed strike increment,
/// and every contract (parsed symbol + quote) inside the bootstrap window. Expiries are snapped to the day
/// they actually trade (holiday Fridays → the prior session), matching the opener's enumerator.</summary>
internal sealed record InitChain(decimal? Spot, decimal StrikeStep, IReadOnlyList<(OptionParsed P, OptionContractQuote Q)> Contracts)
{
	public IReadOnlyList<DateTime> Expiries { get; } = Contracts.Select(c => MarketCalendar.PreviousOpenOnOrBefore(c.P.ExpiryDate)).Distinct().OrderBy(d => d).ToList();
}

/// <summary>Builds the <c>opener.structures</c> block of a scaffolded strategy layer: the chosen structures
/// enabled with chain-derived values, every other structure <c>enabled: false</c>. Only the ticker-dependent
/// knobs are derived — delta bands and other structure settings come from the code defaults, which are already
/// moneyness-relative and so transfer across tickers:
/// <list type="bullet">
/// <item>DTE window — a per-structure horizon, widened to the nearest listed expiry when none falls inside it.</item>
/// <item>Widths — a step count means wildly different things per ticker (2 steps = 0.3% of spot on SPY, 3.8% on
/// PBR), so widths are set as fractions of the 1σ expected move at the window's reference expiry
/// (spot × ATM IV × √(DTE/365)) and converted to strike steps.</item>
/// <item>Iron/long condor body — <c>bodyWidthSteps</c> is a filter on the distance between the two delta-band
/// shorts, so it is read off the chain: the ladder-step distances the band actually produces, ±1 step.</item>
/// </list>
/// The σ fractions are first-principles starting points, not backtested values.</summary>
internal static class AIConfigInitStructures
{
	/// <summary>Structures a <c>--structures</c> list may name, with their derivation recipe. <c>WidthKey</c> is the
	/// structure's wing-width list; <c>Fallback*</c> are used when the chain is unavailable.</summary>
	private sealed record Template(int DteMin, int DteMax, string? WidthKey, decimal[] WidthSigmas, int[] FallbackWidths, bool DerivesBody, int[] FallbackBody);

	private static readonly Dictionary<string, Template> Templates = new(StringComparer.Ordinal)
	{
		["longCallPut"] = new(21, 60, null, [], [], false, []),
		["longVertical"] = new(21, 60, "widthSteps", [0.25m, 0.5m, 1.0m], [2, 4, 8], false, []),
		["shortVertical"] = new(7, 30, "widthSteps", [0.25m, 0.5m, 0.75m], [2, 4, 6], false, []),
		["ironCondor"] = new(7, 30, "widthSteps", [0.25m, 0.5m, 0.75m], [2, 4, 6], true, [4, 6, 10]),
		["condor"] = new(7, 30, "widthSteps", [0.25m, 0.5m, 0.75m], [2, 4], true, [2, 4]),
		["ironButterfly"] = new(7, 30, "wingSteps", [0.5m, 0.75m, 1.0m], [4, 6, 10], false, []),
	};

	/// <summary>The structures scaffolded when <c>--structures</c> is omitted: defined-risk, range-bound credit.</summary>
	public static readonly string[] DefaultStructures = { "ironCondor", "ironButterfly", "shortVertical" };

	/// <summary>Furthest DTE any of <paramref name="structures"/> starts out reaching — sizes the bootstrap fetch.</summary>
	public static int MaxDte(IEnumerable<string> structures) => structures.Select(s => Templates[s].DteMax).DefaultIfEmpty(0).Max();

	/// <summary>Parses a comma-separated <c>--structures</c> value into known template keys (case-insensitive).
	/// Returns an error message naming the unsupported entries when any are unknown.</summary>
	public static (IReadOnlyList<string> Structures, string? Error) Parse(string? csv)
	{
		if (string.IsNullOrWhiteSpace(csv)) return (DefaultStructures, null);
		var known = Templates.Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
		var tokens = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var unknown = tokens.Where(t => !known.ContainsKey(t)).ToList();
		if (unknown.Count > 0) return (Array.Empty<string>(), $"--structures: no derivation for {string.Join(", ", unknown)}. Supported: {string.Join(", ", Templates.Keys)}. Enable other structures by hand in the strategy layer.");
		return (tokens.Select(t => known[t]).ToList(), null);
	}

	/// <summary>Builds the full <c>structures</c> object (every structure the config defines) and a one-line note
	/// per enabled structure describing what was derived. <paramref name="chain"/> null = no live chain: the
	/// fallback widths are used and the DTE windows are left unsnapped.</summary>
	public static (JsonObject Structures, List<string> Notes) Build(IReadOnlyList<string> enabled, InitChain? chain, DateTime today)
	{
		var defaults = JsonSerializer.SerializeToNode(new OpenerStructuresConfig())!.AsObject();
		var structures = new JsonObject();
		var notes = new List<string>();
		foreach (var (key, _) in defaults)
		{
			if (!enabled.Contains(key)) { structures[key] = new JsonObject { ["enabled"] = false }; continue; }
			var (node, note) = BuildOne(key, Templates[key], defaults[key]!.DeepClone().AsObject(), chain, today);
			structures[key] = node;
			notes.Add(note);
		}
		return (structures, notes);
	}

	private static (JsonObject Node, string Note) BuildOne(string key, Template t, JsonObject node, InitChain? chain, DateTime today)
	{
		node["enabled"] = true;
		var listedDtes = chain?.Expiries.Select(e => (e - today).Days).ToList() ?? new List<int>();
		var (dteMin, dteMax) = SnapWindow(t.DteMin, t.DteMax, listedDtes);
		node["dteMin"] = dteMin;
		node["dteMax"] = dteMax;
		var note = $"{key}: DTE {dteMin}-{dteMax}{(dteMin != t.DteMin || dteMax != t.DteMax ? $" (widened from {t.DteMin}-{t.DteMax} to reach a listed expiry)" : "")}";

		var sigma = chain is { Spot: { } spot } ? ReferenceSigma(chain, spot, today, dteMin, dteMax) : null;
		if (t.WidthKey != null)
		{
			var widths = sigma is { } s ? t.WidthSigmas.Select(f => Math.Max(1, (int)Math.Round(f * s.Sigma / chain!.StrikeStep, MidpointRounding.AwayFromZero))).Distinct().OrderBy(w => w).ToArray() : t.FallbackWidths;
			node[t.WidthKey] = ToArray(widths);
			note += sigma is { } r
				? $" · {t.WidthKey} {Format(widths)} = {string.Join("/", t.WidthSigmas.Select(f => f.ToString("0.##", CultureInfo.InvariantCulture)))}σ of ${r.Sigma:0.00} (1σ to {r.Expiry:yyyy-MM-dd}, IV {r.Iv:0%})"
				: $" · {t.WidthKey} {Format(widths)} (fallback — no live chain)";
		}
		if (t.DerivesBody)
		{
			var body = chain is { Spot: { } sp } ? BodySteps(chain, sp, today, dteMin, dteMax, node["shortDeltaMin"]!.GetValue<decimal>(), node["shortDeltaMax"]!.GetValue<decimal>()) : null;
			node["bodyWidthSteps"] = ToArray(body ?? t.FallbackBody);
			note += $" · bodyWidthSteps {Format(body ?? t.FallbackBody)}{(body == null ? " (fallback)" : " (delta-band shorts on the chain, ±1)")}";
		}
		return (node, note);
	}

	/// <summary>Leaves the window as-is when a listed expiry falls inside it; otherwise widens the far edge out to
	/// the next listed expiry, or (none further out) the near edge in to the last one before it.</summary>
	private static (int Min, int Max) SnapWindow(int min, int max, IReadOnlyList<int> listedDtes)
	{
		if (listedDtes.Count == 0 || listedDtes.Any(d => d >= min && d <= max)) return (min, max);
		var next = listedDtes.Where(d => d > max).DefaultIfEmpty(-1).Min();
		if (next >= 0) return (min, next);
		var prior = listedDtes.Where(d => d < min && d >= 0).DefaultIfEmpty(-1).Max();
		return prior >= 0 ? (prior, max) : (min, max);
	}

	/// <summary>1σ expected move (dollars) to the listed expiry nearest the window's midpoint, from that expiry's
	/// ATM implied vol. Null when no expiry in the window has a usable ATM IV.</summary>
	private static (decimal Sigma, DateTime Expiry, decimal Iv)? ReferenceSigma(InitChain chain, decimal spot, DateTime today, int dteMin, int dteMax)
	{
		var mid = (dteMin + dteMax) / 2.0;
		foreach (var exp in chain.Expiries.Where(e => (e - today).Days >= dteMin && (e - today).Days <= dteMax).OrderBy(e => Math.Abs((e - today).Days - mid)))
		{
			if (AtmIv(chain, exp, spot) is not { } iv) continue;
			var sigma = spot * iv * (decimal)Math.Sqrt((exp - today).Days / 365.0);
			return (sigma, exp, iv);
		}
		return null;
	}

	/// <summary>ATM implied vol (0-1 fraction) at the strike nearest spot on <paramref name="expiry"/>; null when none is positive.</summary>
	public static decimal? AtmIv(InitChain chain, DateTime expiry, decimal spot) => chain.Contracts
		.Where(c => MarketCalendar.PreviousOpenOnOrBefore(c.P.ExpiryDate) == expiry && c.Q.ImpliedVolatility is > 0m)
		.OrderBy(c => Math.Abs(c.P.Strike - spot))
		.Select(c => c.Q.ImpliedVolatility)
		.FirstOrDefault() is { } v ? Math.Round(v, 4) : null;

	/// <summary>The ladder-step distances between a put short and a call short both inside the short-delta band,
	/// over every listed expiry in the window, as a contiguous range widened by one step each side (spot drifts
	/// between scaffold and scan). Deltas use each strike's own chain IV, as the enumerator does. Null when the
	/// band holds no put/call short pair.</summary>
	private static int[]? BodySteps(InitChain chain, decimal spot, DateTime today, int dteMin, int dteMax, decimal deltaMin, decimal deltaMax)
	{
		var bodies = new List<int>();
		var anchor = OptionMath.ObservationInstant();
		foreach (var exp in chain.Expiries.Where(e => (e - today).Days >= dteMin && (e - today).Days <= dteMax))
		{
			var legs = chain.Contracts.Where(c => MarketCalendar.PreviousOpenOnOrBefore(c.P.ExpiryDate) == exp).ToList();
			var ladder = legs.Select(c => c.P.Strike).Distinct().OrderBy(k => k).ToList();
			var years = (exp.Date + OptionMath.MarketClose - anchor).TotalDays / 365.0;
			if (years <= 0) continue;
			var inBand = legs
				.Where(c => c.Q.ImpliedVolatility is > 0m && (c.P.CallPut == "P" ? c.P.Strike < spot : c.P.Strike > spot))
				.Where(c => Math.Abs(OptionMath.Delta(spot, c.P.Strike, years, OptionMath.RiskFreeRate, c.Q.ImpliedVolatility!.Value, c.P.CallPut)) is var d && d >= deltaMin && d <= deltaMax)
				.ToList();
			foreach (var put in inBand.Where(c => c.P.CallPut == "P"))
				foreach (var call in inBand.Where(c => c.P.CallPut == "C"))
					bodies.Add(ladder.IndexOf(call.P.Strike) - ladder.IndexOf(put.P.Strike));
		}
		if (bodies.Count == 0) return null;
		var lo = Math.Max(1, bodies.Min() - 1);
		return Enumerable.Range(lo, bodies.Max() + 1 - lo + 1).ToArray();
	}

	private static JsonArray ToArray(IEnumerable<int> values) => new(values.Select(v => (JsonNode)v).ToArray());

	private static string Format(IEnumerable<int> values) => $"[{string.Join(", ", values)}]";
}
