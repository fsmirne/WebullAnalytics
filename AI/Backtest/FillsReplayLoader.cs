using System.Globalization;
using System.Text.Json;

namespace WebullAnalytics.AI.Backtest;

/// <summary>
/// Reads a prior backtest's <c>--fills-jsonl</c> ledger and returns its Open fills as replay opens (<c>--opens</c>):
/// same minute, legs, qty, spot and scores, leg prices as booked (pre-slippage — slippage is re-applied on open so the
/// cash flow matches the source run). Skipping the opener makes a management-only sweep (exit rules) cost only the
/// management walk. Exact when the source run used the same opener config and the opens don't depend on what the
/// exits changed — with <c>--lots</c> the cash gate is off, but held-position gates (HeldLegGuard, add-to-held)
/// still read the book, so validate a new exit rule once against a full run before trusting replayed sweeps.
/// </summary>
internal static class FillsReplayLoader
{
	public static (IReadOnlyList<ProposalReplayOpen> Opens, IReadOnlyList<string> Warnings) Load(string path, DateTime since, DateTime until)
	{
		var opens = new List<ProposalReplayOpen>();
		var warnings = new List<string>();
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
				if (!string.Equals(Str(root, "kind"), "Open", StringComparison.Ordinal)) continue;
				if (!DateTime.TryParseExact(Str(root, "ts"), "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var openEt)) { warnings.Add($"{file}:{lineNo}: bad ts — skipped"); continue; }
				if (openEt.Date < since.Date || openEt.Date > until.Date) continue;
				if (!Enum.TryParse<OpenStructureKind>(Str(root, "strategy"), ignoreCase: false, out var kind)) { warnings.Add($"{file}:{lineNo}: unknown strategy '{Str(root, "strategy")}' — skipped"); continue; }
				if (!root.TryGetProperty("legs", out var legsEl) || legsEl.ValueKind != JsonValueKind.Array) { warnings.Add($"{file}:{lineNo}: no legs — skipped"); continue; }

				var legs = new List<ProposalLeg>();
				foreach (var l in legsEl.EnumerateArray())
				{
					var action = string.Equals(Str(l, "side"), "Buy", StringComparison.Ordinal) ? "buy" : "sell";
					legs.Add(new ProposalLeg(action, Str(l, "sym")!, (int)Dec(l, "qty")!.Value, Dec(l, "price")));
				}
				opens.Add(new ProposalReplayOpen(openEt, Str(root, "ticker")!, kind, (int)Dec(root, "qty")!.Value, legs, Dec(root, "spot") ?? 0m, Dec(root, "rawScore"), Dec(root, "finalScore"), Dec(root, "iv"), ApplySlippage: true));
			}
		}
		return (opens, warnings);
	}

	private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	private static decimal? Dec(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
