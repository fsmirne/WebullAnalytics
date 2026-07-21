using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics.AI.Backtest;

/// <summary>One live-recorded open proposal selected for replay: the trade the opener would have placed with
/// submit on. Leg <c>PricePerShare</c> is the recorded vendor-quote mid with the executor's per-order tick
/// rounding re-applied, i.e. the limit price the order would have been submitted (and assumed filled) at —
/// <c>ExecutionPricePerShare</c> is set to the same value so the backtest's <c>--pricing</c> mode cannot
/// re-price the entry.</summary>
internal sealed record ProposalReplayOpen(DateTime OpenEt, string Ticker, OpenStructureKind StructureKind, int Qty, IReadOnlyList<ProposalLeg> Legs, decimal Spot, decimal? RawScore, decimal? FinalScore);

/// <summary>
/// Reads an <c>ai-proposals.&lt;TICKER&gt;.&lt;strategy&gt;.jsonl</c> log and selects, per trading day, the open
/// proposal the live executor would have placed: the first <c>type=open</c> record at/after 09:30 ET that is
/// not informational (below <c>minScoreToOpen</c>), not cash-reserve-blocked, and sized to at least 1 contract.
/// Records are scanned in file order, which is the sink's emission order (rank order within a tick), so the
/// first qualifying record of a day is that day's top-ranked actionable proposal. Entry prices come from the
/// stored <c>diagnostic.probe.legQuotes</c> bid/ask mids — the same numbers the scorer priced the fill from —
/// with <see cref="OptionPriceRounding"/> applied per order group exactly as <c>OpenerAutoExecutor</c> rounds
/// its submit limit. A day whose chosen record cannot be replayed (missing leg quotes, unknown structure)
/// yields a warning and NO trade — falling through to a lower-ranked record would replay a trade the live
/// executor never picked.
/// </summary>
internal static class ProposalReplayLoader
{
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
	private static readonly TimeSpan MarketOpen = new(9, 30, 0);
	private static readonly TimeSpan MarketClose = new(16, 0, 0);

	public static (IReadOnlyList<ProposalReplayOpen> Opens, IReadOnlyList<string> Warnings) Load(string path, DateTime since, DateTime until, decimal minScoreToOpen)
	{
		var opens = new List<ProposalReplayOpen>();
		var warnings = new List<string>();
		var decidedDates = new HashSet<DateTime>();
		var file = Path.GetFileName(path);
		var lineNo = 0;

		foreach (var line in WebullAnalytics.IO.SharedFileReader.ReadLines(path))
		{
			lineNo++;
			if (string.IsNullOrWhiteSpace(line)) continue;

			JsonDocument doc;
			try { doc = JsonDocument.Parse(line); }
			catch (JsonException) { warnings.Add($"{file}:{lineNo}: unparseable JSON — skipped"); continue; }

			using (doc)
			{
				var root = doc.RootElement;
				if (!string.Equals(Str(root, "type") ?? "open", "open", StringComparison.OrdinalIgnoreCase)) continue;

				var openEt = ParseEasternTs(Str(root, "ts"));
				if (openEt == null || openEt.Value.Date < since.Date || openEt.Value.Date > until.Date) continue;
				if (openEt.Value.TimeOfDay < MarketOpen || openEt.Value.TimeOfDay >= MarketClose) continue;
				if (decidedDates.Contains(openEt.Value.Date)) continue;

				// Live-executor gates. `informational` is authoritative on records new enough to carry it; the
				// score-vs-gate comparison covers older logs (exact for an unchanged config). Cash-blocked
				// proposals render but are never placed; qty<1 can't size an order.
				if (root.TryGetProperty("informational", out var inf) ? inf.ValueKind == JsonValueKind.True : (Dec(root, "finalScore") ?? 0m) < minScoreToOpen) continue;
				if (root.TryGetProperty("cashReserveBlocked", out var crb) && crb.ValueKind == JsonValueKind.True) continue;
				var qty = (int)(Dec(root, "qty") ?? 0m);
				if (qty < 1) continue;

				// This record is the day's pick — from here on any failure produces a warning, not a fallback.
				decidedDates.Add(openEt.Value.Date);

				var structureName = Str(root, "structure");
				if (!Enum.TryParse<OpenStructureKind>(structureName, ignoreCase: true, out var kind))
				{
					warnings.Add($"{file}:{lineNo}: unknown structure '{structureName}' — no trade replayed for {openEt.Value:yyyy-MM-dd}");
					continue;
				}

				var legQuotes = new Dictionary<string, (decimal? Bid, decimal? Ask)>(StringComparer.OrdinalIgnoreCase);
				if (root.TryGetProperty("diagnostic", out var diag) && diag.ValueKind == JsonValueKind.Object && diag.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object && probe.TryGetProperty("legQuotes", out var lq) && lq.ValueKind == JsonValueKind.Array)
					foreach (var q in lq.EnumerateArray())
						if (Str(q, "symbol") is string sym)
							legQuotes[sym] = (Dec(q, "bid"), Dec(q, "ask"));

				if (!root.TryGetProperty("legs", out var legsEl) || legsEl.ValueKind != JsonValueKind.Array)
				{
					warnings.Add($"{file}:{lineNo}: record has no legs — no trade replayed for {openEt.Value:yyyy-MM-dd}");
					continue;
				}

				var legs = new List<ProposalLeg>();
				string? unpriced = null;
				foreach (var l in legsEl.EnumerateArray())
				{
					var symbol = Str(l, "symbol");
					var action = Str(l, "action");
					if (symbol == null || action == null) { unpriced = "leg missing symbol/action"; break; }
					if (!legQuotes.TryGetValue(symbol, out var quote) || (quote.Bid ?? quote.Ask) == null) { unpriced = $"no stored quote for leg {symbol}"; break; }
					var mid = quote.Bid.HasValue && quote.Ask.HasValue ? (quote.Bid.Value + quote.Ask.Value) / 2m : (quote.Bid ?? quote.Ask)!.Value;
					legs.Add(new ProposalLeg(action, symbol, (int)(Dec(l, "qty") ?? 1m), mid, mid));
				}
				if (unpriced != null || legs.Count == 0)
				{
					warnings.Add($"{file}:{lineNo}: {unpriced ?? "record has no legs"} — no trade replayed for {openEt.Value:yyyy-MM-dd}");
					continue;
				}

				var ticker = Str(root, "ticker") ?? ParsingHelpers.ParseOptionSymbol(legs[0].Symbol)?.Root ?? "";
				RoundGroupsToTick(legs, kind, ticker);
				var spot = root.TryGetProperty("diagnostic", out var d2) && d2.ValueKind == JsonValueKind.Object ? Dec(d2, "spotAtEvaluation") ?? 0m : 0m;
				opens.Add(new ProposalReplayOpen(openEt.Value, ticker, kind, qty, legs, spot, Dec(root, "rawScore"), Dec(root, "finalScore")));
			}
		}
		return (opens, warnings);
	}

	/// <summary>Re-applies the executor's submit rounding: per order group (see <see cref="StructureOrderSplit"/>),
	/// the signed net of the leg mids is rounded to the exchange tick, and the (at most half-tick) delta is
	/// absorbed into the group's highest-priced leg so the leg-fill sum equals the submitted combo limit exactly.</summary>
	private static void RoundGroupsToTick(List<ProposalLeg> legs, OpenStructureKind kind, string ticker)
	{
		foreach (var (_, groupLegs) in StructureOrderSplit.Split(kind, legs))
		{
			decimal rawNet = 0m;
			foreach (var l in groupLegs) rawNet += string.Equals(l.Action, "buy", StringComparison.OrdinalIgnoreCase) ? -l.PricePerShare!.Value : l.PricePerShare!.Value;
			var limitAbs = OptionPriceRounding.RoundToTick(Math.Abs(rawNet), groupLegs.Count, ticker);
			var delta = (rawNet >= 0m ? limitAbs : -limitAbs) - rawNet;
			if (delta == 0m) continue;
			var target = groupLegs.OrderByDescending(l => l.PricePerShare!.Value).First();
			var dp = string.Equals(target.Action, "buy", StringComparison.OrdinalIgnoreCase) ? -delta : delta;
			var adjusted = Math.Max(0m, target.PricePerShare!.Value + dp);
			legs[legs.IndexOf(target)] = target with { PricePerShare = adjusted, ExecutionPricePerShare = adjusted };
		}
	}

	/// <summary>The sink stamps <c>ts</c> with <c>DateTime.Now.ToString("o")</c>, which carries the machine's
	/// UTC offset — convert to ET wall clock to match the simulator's convention.</summary>
	private static DateTime? ParseEasternTs(string? ts)
	{
		if (string.IsNullOrWhiteSpace(ts)) return null;
		return DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) ? TimeZoneInfo.ConvertTime(dto, NyTz).DateTime : null;
	}

	private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
	private static decimal? Dec(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
