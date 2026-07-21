using System.Globalization;
using System.Text.Json;
using WebullAnalytics.AI;
using WebullAnalytics.AI.RiskDiagnostics;
using WebullAnalytics.Trading;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Rehydrates a single proposal line from an <c>ai-proposals.&lt;TICKER&gt;.&lt;strategy&gt;.jsonl</c> log into the
/// inputs the analyze commands render from — legs, spot, evaluation instant, per-leg cost basis and the stored
/// <see cref="RiskDiagnostic"/>. <c>analyze risk --proposal</c> replays the stored diagnostic exactly as it
/// stood when the opener/manager emitted it; <c>analyze position|trade --proposal</c> take only the legs and
/// entry cost basis from here and re-evaluate them against the live market now.
/// Handles both <c>type=open</c> (opener) and <c>type=management</c> (manage) records; the two differ only in
/// where the leg list lives (<c>legs</c> vs <c>proposal.legs</c>).
/// </summary>
internal sealed class ProposalSnapshot
{
	internal sealed record SnapshotLeg(LegAction Action, string Symbol, int Qty, OptionParsed Parsed);

	public string Ticker { get; private init; } = "";
	public string Strategy { get; private init; } = "";
	public string StructureLabel { get; private init; } = "";
	public DateTime AsOf { get; private init; }
	public decimal Spot { get; private init; }
	public IReadOnlyList<SnapshotLeg> Legs { get; private init; } = Array.Empty<SnapshotLeg>();
	public RiskDiagnostic? Diagnostic { get; private init; }
	public string SourcePath { get; private init; } = "";
	public int LineNumber { get; private init; }

	private IReadOnlyDictionary<string, RiskDiagnosticLegQuote> _legQuotes = new Dictionary<string, RiskDiagnosticLegQuote>(StringComparer.OrdinalIgnoreCase);

	/// <summary>Per-leg cost basis: the bid/ask mid captured at proposal time (how the opener priced the fill).
	/// Falls back to whichever side is quoted, then 0 when neither is.</summary>
	public decimal CostBasis(string symbol)
	{
		if (!_legQuotes.TryGetValue(symbol, out var q)) return 0m;
		if (q.Bid.HasValue && q.Ask.HasValue) return (q.Bid.Value + q.Ask.Value) / 2m;
		return q.Bid ?? q.Ask ?? 0m;
	}

	/// <summary>Loads and parses one proposal line. <paramref name="spec"/> is <c>FILE[:LINE]</c>: FILE is a
	/// path, a bare <c>ai-proposals.*.jsonl</c> filename resolved under <c>data/</c>, or the <c>TICKER.strategy</c>
	/// shorthand; LINE is a 1-based line number, defaulting to the last line. Returns a clean error string
	/// (never throws) so callers can surface it and exit.</summary>
	public static (ProposalSnapshot? Snapshot, string? Error) TryLoad(string spec)
	{
		if (string.IsNullOrWhiteSpace(spec)) return (null, "--proposal: empty spec");
		var raw = spec.Trim();

		// Split a trailing ":<line>" only when the suffix is a positive integer, so Windows drive letters and
		// filenames with colons stay intact.
		var file = raw;
		var lineNumber = -1;
		var colon = raw.LastIndexOf(':');
		if (colon > 0 && int.TryParse(raw[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLine) && parsedLine > 0)
		{
			file = raw[..colon];
			lineNumber = parsedLine;
		}

		var resolved = ResolveFile(file);
		if (resolved == null)
			return (null, $"--proposal: no proposal file found for '{file}'. Pass a path, a data/ filename (ai-proposals.SPY.0DTE.jsonl), or the TICKER.strategy shorthand (SPY.0DTE).");

		string[] lines;
		try { lines = WebullAnalytics.IO.SharedFileReader.ReadAllLines(resolved); }
		catch (Exception ex) { return (null, $"--proposal: failed to read '{resolved}': {ex.Message}"); }

		string? lineText;
		int chosenLine;
		if (lineNumber > 0)
		{
			if (lineNumber > lines.Length) return (null, $"--proposal: line {lineNumber} is out of range ({resolved} has {lines.Length} lines)");
			lineText = lines[lineNumber - 1];
			chosenLine = lineNumber;
		}
		else
		{
			chosenLine = -1;
			lineText = null;
			for (var i = lines.Length - 1; i >= 0; i--)
				if (!string.IsNullOrWhiteSpace(lines[i])) { lineText = lines[i]; chosenLine = i + 1; break; }
			if (lineText == null) return (null, $"--proposal: '{resolved}' has no non-empty lines");
		}

		if (string.IsNullOrWhiteSpace(lineText)) return (null, $"--proposal: line {chosenLine} of '{resolved}' is empty");

		try { return (Parse(lineText, resolved, chosenLine), null); }
		catch (Exception ex) { return (null, $"--proposal: line {chosenLine} of '{resolved}' is not a valid proposal: {ex.Message}"); }
	}

	private static string? ResolveFile(string file)
	{
		var candidates = new List<string> { file, Program.ResolvePath(file), Program.ResolvePath(Path.Combine("data", file)) };
		if (!file.Contains('/') && !file.Contains('\\') && !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
		{
			var dot = file.IndexOf('.');
			if (dot > 0 && dot < file.Length - 1)
				candidates.Add(ProposalLog.ResolvedPath(file[..dot], file[(dot + 1)..]));
		}
		return candidates.FirstOrDefault(File.Exists);
	}

	private static ProposalSnapshot Parse(string lineText, string sourcePath, int lineNumber)
	{
		using var doc = JsonDocument.Parse(lineText);
		var root = doc.RootElement;
		var type = Str(root, "type") ?? "open";

		var legsEl = type.Equals("management", StringComparison.OrdinalIgnoreCase)
			? root.GetProperty("proposal").GetProperty("legs")
			: root.GetProperty("legs");

		var legs = new List<SnapshotLeg>();
		foreach (var l in legsEl.EnumerateArray())
		{
			var symbol = Str(l, "symbol") ?? throw new FormatException("leg is missing 'symbol'");
			var parsed = ParsingHelpers.ParseOptionSymbol(symbol) ?? throw new FormatException($"leg '{symbol}' is not an OCC option symbol");
			var action = string.Equals(Str(l, "action"), "buy", StringComparison.OrdinalIgnoreCase) ? LegAction.Buy : LegAction.Sell;
			var qty = l.TryGetProperty("qty", out var qtyEl) && qtyEl.ValueKind == JsonValueKind.Number ? qtyEl.GetInt32() : 1;
			legs.Add(new SnapshotLeg(action, symbol, qty, parsed));
		}
		if (legs.Count == 0) throw new FormatException("proposal has no legs");

		var diagnostic = root.TryGetProperty("diagnostic", out var diagEl) && diagEl.ValueKind == JsonValueKind.Object
			? ParseDiagnostic(diagEl)
			: null;

		var spot = diagnostic?.SpotAtEvaluation ?? 0m;
		var asOf = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String && DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
			? ts
			: DateTime.Now;

		var structureLabel = Str(root, "structure") ?? diagnostic?.StructureLabel ?? "Unknown";

		var legQuotes = diagnostic?.Probe?.LegQuotes?.ToDictionary(q => q.Symbol, q => q, StringComparer.OrdinalIgnoreCase)
			?? new Dictionary<string, RiskDiagnosticLegQuote>(StringComparer.OrdinalIgnoreCase);

		return new ProposalSnapshot
		{
			Ticker = Str(root, "ticker") ?? legs[0].Parsed.Root,
			Strategy = Str(root, "strategy") ?? "",
			StructureLabel = structureLabel,
			AsOf = asOf,
			Spot = spot,
			Legs = legs,
			Diagnostic = diagnostic,
			SourcePath = sourcePath,
			LineNumber = lineNumber,
			_legQuotes = legQuotes,
		};
	}

	// ─── RiskDiagnostic rehydration (inverse of AnalyzePositionCommand.SerializeDiagnostic) ──────────

	private static RiskDiagnostic ParseDiagnostic(JsonElement d) => new(
		StructureLabel: Str(d, "structureLabel") ?? "",
		DirectionalBias: Str(d, "directionalBias") ?? "",
		NetDelta: Dec(d, "netDelta"),
		NetThetaPerDay: Dec(d, "netThetaPerDay"),
		NetVega: Dec(d, "netVega"),
		ShortLegDteMin: Int(d, "shortLegDteMin"),
		LongLegDteMax: Int(d, "longLegDteMax"),
		DteGapDays: Int(d, "dteGapDays"),
		LongPremiumPaid: Dec(d, "longPremiumPaid"),
		ShortPremiumReceived: Dec(d, "shortPremiumReceived"),
		NetCashPerShare: Dec(d, "netCashPerShare"),
		PremiumRatio: DecN(d, "premiumRatio"),
		SpotAtEvaluation: Dec(d, "spotAtEvaluation"),
		BreakevenDistancePct: null,
		ShortLegOtm: Bool(d, "shortLegOtm"),
		ShortLegExtrinsic: Dec(d, "shortLegExtrinsic"),
		Trend: d.TryGetProperty("trend", out var t) && t.ValueKind == JsonValueKind.Object ? ParseTrend(t) : null,
		CostBasisPerShare: DecN(d, "costBasisPerShare"),
		CurrentValuePerShare: DecN(d, "currentValuePerShare"),
		UnrealizedPnlPerShare: DecN(d, "unrealizedPnlPerShare"),
		Rules: d.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array ? ParseRules(r) : Array.Empty<RiskRuleHit>(),
		Probe: d.TryGetProperty("probe", out var p) && p.ValueKind == JsonValueKind.Object ? ParseProbe(p) : null,
		NetMidPerShare: DecN(d, "netMidPerShare"),
		TheoreticalValuePerShare: DecN(d, "theoreticalValuePerShare"),
		MarketLongPremiumPaid: DecN(d, "marketLongPremiumPaid"),
		MarketShortPremiumReceived: DecN(d, "marketShortPremiumReceived"),
		MarketNetPremiumPerShare: DecN(d, "marketNetPremiumPerShare"),
		MarketPremiumRatio: DecN(d, "marketPremiumRatio"),
		TheoreticalLongPremiumPaid: DecN(d, "theoreticalLongPremiumPaid"),
		TheoreticalShortPremiumReceived: DecN(d, "theoreticalShortPremiumReceived"),
		TheoreticalNetPremiumPerShare: DecN(d, "theoreticalNetPremiumPerShare"),
		TheoreticalPremiumRatio: DecN(d, "theoreticalPremiumRatio"),
		MarketSentimentScore: DecN(d, "marketSentimentScore"),
		MarketSentimentRating: Str(d, "marketSentimentRating"),
		MarketSentimentDelta1Week: DecN(d, "marketSentimentDelta1Week"));

	private static TrendSnapshot ParseTrend(JsonElement t) => new(
		ChangePctIntraday: DecN(t, "changePctIntraday"),
		ChangePct5Day: Dec(t, "changePct5Day"),
		ChangePct20Day: Dec(t, "changePct20Day"),
		Atr14Pct: Dec(t, "atr14Pct"),
		AsOf: t.TryGetProperty("asOf", out var a) && a.ValueKind == JsonValueKind.String && DateTime.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var asOf) ? asOf : default);

	private static RiskDiagnosticProbe ParseProbe(JsonElement p)
	{
		var legQuotes = new List<RiskDiagnosticLegQuote>();
		if (p.TryGetProperty("legQuotes", out var lq) && lq.ValueKind == JsonValueKind.Array)
			foreach (var q in lq.EnumerateArray())
				legQuotes.Add(new RiskDiagnosticLegQuote(
					Label: Str(q, "label") ?? "",
					Symbol: Str(q, "symbol") ?? "",
					Bid: DecN(q, "bid"),
					Ask: DecN(q, "ask"),
					ImpliedVolatility: DecN(q, "impliedVolatility"),
					HistoricalVolatility: DecN(q, "historicalVolatility"),
					OpenInterest: LongN(q, "openInterest"),
					Volume: LongN(q, "volume")));

		RiskDiagnosticOpenerScore? opener = null;
		if (p.TryGetProperty("openerScore", out var o) && o.ValueKind == JsonValueKind.Object)
			opener = new RiskDiagnosticOpenerScore(
				Structure: Str(o, "structure") ?? "",
				Qty: Int(o, "qty"),
				DebitOrCreditPerContract: DecN(o, "debitOrCreditPerContract"),
				MaxProfitPerContract: DecN(o, "maxProfitPerContract"),
				MaxLossPerContract: DecN(o, "maxLossPerContract"),
				CapitalAtRiskPerContract: DecN(o, "capitalAtRiskPerContract"),
				ProbabilityOfProfit: DecN(o, "probabilityOfProfit"),
				ExpectedValuePerContract: DecN(o, "expectedValuePerContract"),
				DaysToTarget: IntN(o, "daysToTarget"),
				RawScore: DecN(o, "rawScore"),
				BiasAdjustedScore: DecN(o, "biasAdjustedScore"),
				Rationale: Str(o, "rationale"));

		return new RiskDiagnosticProbe(
			EnumDelta: DecN(p, "enumDelta"),
			EnumDeltaMin: DecN(p, "enumDeltaMin"),
			EnumDeltaMax: DecN(p, "enumDeltaMax"),
			EnumDeltaPass: p.TryGetProperty("enumDeltaPass", out var ep) && ep.ValueKind == JsonValueKind.True ? true : p.TryGetProperty("enumDeltaPass", out var ef) && ef.ValueKind == JsonValueKind.False ? false : (bool?)null,
			LegQuotes: legQuotes,
			OpenerScore: opener);
	}

	private static IReadOnlyList<RiskRuleHit> ParseRules(JsonElement r)
	{
		var rules = new List<RiskRuleHit>();
		foreach (var rule in r.EnumerateArray())
		{
			var inputs = new Dictionary<string, decimal>();
			if (rule.TryGetProperty("inputs", out var inp) && inp.ValueKind == JsonValueKind.Object)
				foreach (var kv in inp.EnumerateObject())
					if (kv.Value.ValueKind == JsonValueKind.Number) inputs[kv.Name] = kv.Value.GetDecimal();
			rules.Add(new RiskRuleHit(Str(rule, "id") ?? "", Str(rule, "message") ?? "", inputs));
		}
		return rules;
	}

	// ─── JsonElement accessors ──────────────────────────────────────────────────────────────────────

	private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	private static decimal Dec(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
	private static decimal? DecN(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
	private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
	private static int? IntN(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
	private static long? LongN(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
	private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
