using Spectre.Console;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.AI;

/// <summary>Backfills per-contract option minute bars from Webull's <c>/api/quote/option/chart/kdata</c>
/// endpoint. Each contract's bars (with implied volatility) are written to
/// <c>data/options/&lt;root&gt;/&lt;expiry&gt;/&lt;occ&gt;.csv</c>. Driven by <see cref="DerivativeIdRegistry"/>
/// — the registry only contains contracts we've observed live in a chain fetch, so this command
/// can't conjure data for contracts that expired before we started persisting ids.
///
/// <para>Pacing: 200 ms between contract fetches (5 req/sec). Empirically Webull's chart endpoint
/// handles bursts well above this — the underlying SPX query-mini pagination runs at 1 req/sec for
/// politeness, and the option chart endpoint has not shown signs of stricter rate-limiting in probes —
/// but 5 req/sec gives plenty of headroom and the user can tune via <c>--pace-ms</c> if needed.</para>
///
/// <para>Idempotent: skips contracts whose CSV already exists unless <c>--force</c> is set. So
/// re-running the command after a partial backfill picks up where it left off without re-fetching
/// completed contracts.</para></summary>
internal static class AIHistoryOptionsBackfill
{
	private static readonly TimeSpan DefaultPace = TimeSpan.FromMilliseconds(200);

	public static async Task<int> RunAsync(string ticker, bool force, bool all, CancellationToken cancellation)
	{
		AnsiConsole.MarkupLine($"[bold]Option backfill for {Markup.Escape(ticker)}[/]");

		var apiConfigPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(apiConfigPath))
		{
			AnsiConsole.MarkupLine("  [red]api-config.json not found[/] — run `wa sniff` to bootstrap it");
			return 1;
		}
		ApiConfig? apiConfig;
		try
		{
			apiConfig = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(apiConfigPath));
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"  [red]failed to parse api-config.json[/]: {Markup.Escape(ex.Message)}");
			return 1;
		}
		if (apiConfig == null || apiConfig.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("  [red]api-config.json has no headers[/] — run `wa sniff` to refresh");
			return 1;
		}

		var registry = DerivativeIdRegistry.Snapshot();
		if (registry.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [yellow]derivative-id registry is empty[/] — run `wa ai watch` or `wa analyze` first to populate it from a live chain fetch");
			return 0;
		}

		// Filter registry to contracts whose root matches the requested ticker. The registry is keyed
		// by OCC symbol, so we parse each one and compare roots case-insensitively.
		var matches = new List<(string Occ, long DerivativeId, OptionParsed Parsed)>();
		foreach (var (occ, id) in registry)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed == null) continue;
			if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			matches.Add((occ, id, parsed));
		}

		if (matches.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [yellow]no {Markup.Escape(ticker)} contracts in derivative-id registry[/] ({registry.Count} total entries, none with root '{Markup.Escape(ticker)}')");
			return 0;
		}

		// Default: filter down to contracts the bot has actually proposed or that were traded. This is
		// usually <100 symbols for a few weeks of live runs, compared to ~20K for the full chain. The
		// non-touched strikes are mostly illiquid wings the live bot would never pick, so backfilling
		// them is bandwidth + time we'd never use. Override with --all to ingest the full chain.
		if (!all)
		{
			var touched = LoadTouchedSymbols(ticker);
			var before = matches.Count;
			matches = matches.Where(m => touched.Contains(m.Occ)).ToList();
			AnsiConsole.MarkupLine($"  filter: keeping {matches.Count}/{before} contract(s) that appear in ai-proposals.jsonl / orders.jsonl (use --all to backfill the full chain)");
			if (matches.Count == 0)
			{
				AnsiConsole.MarkupLine($"  [yellow]no touched contracts to backfill[/] — either nothing in proposals/orders for {Markup.Escape(ticker)}, or none of those symbols are in the registry yet");
				return 0;
			}
		}

		var optionsRoot = Path.Combine(Program.ResolvePath("data/options"), ticker.ToUpperInvariant());

		// Every match is fetched — merge-on-rerun is the default so re-running picks up new minutes
		// without losing existing ones. Pre-classify into "fresh" (no CSV on disk) vs "merge" (CSV
		// exists and --force not set) so the progress output can report each category accurately;
		// merge contracts whose fetch returns nothing-new get reported as "unchanged" rather than
		// "written" so users can see the run was actually idempotent on a no-change pass.
		var work = new List<(string Occ, long DerivativeId, OptionParsed Parsed, string CsvPath, bool MergeExisting)>();
		foreach (var (occ, id, parsed) in matches)
		{
			var csvPath = BuildCsvPath(optionsRoot, parsed, occ);
			var mergeExisting = !force && File.Exists(csvPath);
			work.Add((occ, id, parsed, csvPath, mergeExisting));
		}
		var freshCount = work.Count(w => !w.MergeExisting);
		var mergeCount = work.Count(w => w.MergeExisting);
		AnsiConsole.MarkupLine($"  registry matches: {matches.Count} ({freshCount} fresh, {mergeCount} merge with existing{(force ? "; --force overrides merge" : "")})");

		// Order by expiry then strike for predictable progress output — the user can tell at a glance
		// which expiration window the backfill is currently chewing through.
		work.Sort((a, b) =>
		{
			var c = a.Parsed.ExpiryDate.CompareTo(b.Parsed.ExpiryDate);
			return c != 0 ? c : a.Parsed.Strike.CompareTo(b.Parsed.Strike);
		});

		var written = 0;
		var merged = 0;
		var unchanged = 0;
		var emptyResponses = new List<string>();
		var failures = new List<(string Occ, string Reason)>();
		var index = 0;
		foreach (var (occ, id, parsed, csvPath, mergeExisting) in work)
		{
			cancellation.ThrowIfCancellationRequested();
			if (index > 0) await Task.Delay(DefaultPace, cancellation);
			index++;

			IReadOnlyList<OptionMinuteBar> bars;
			try
			{
				// No anchor → endpoint returns the most-recent 800 bars for this contract. For 0DTE
				// SPXW that's plenty (a full RTH day = 390 minutes). For longer-dated contracts it
				// covers ~2 trading days; we can extend with a paginated walk later if we ever need
				// long-DTE backtests with full intraday history per contract.
				bars = await WebullChartsClient.FetchOptionContractMinuteBarsAsync(apiConfig!, id, 800, anchorUnixSec: null, cancellation);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				failures.Add((occ, ex.Message));
				continue;
			}

			if (bars.Count == 0)
			{
				emptyResponses.Add(occ);
				continue;
			}

			if (mergeExisting)
			{
				var existing = ReadOptionCsv(csvPath);
				var (mergedBars, newMinutes) = MergeByTimestamp(existing, bars);
				if (newMinutes == 0) { unchanged++; }
				else
				{
					WriteOptionCsv(csvPath, mergedBars);
					merged++;
				}
			}
			else
			{
				WriteOptionCsv(csvPath, bars);
				written++;
			}

			if (index % 25 == 0 || index == work.Count)
				AnsiConsole.MarkupLine($"    progress: {index}/{work.Count} (wrote {written}, merged {merged}, unchanged {unchanged}, empty {emptyResponses.Count}, failed {failures.Count})");
		}

		AnsiConsole.MarkupLine($"  [green]wrote {written}[/] new + [green]merged {merged}[/] existing CSV(s) under {Markup.Escape(optionsRoot)}{(unchanged > 0 ? $" ({unchanged} unchanged)" : "")}");
		if (emptyResponses.Count > 0)
		{
			var preview = string.Join(", ", emptyResponses.Take(8));
			var more = emptyResponses.Count > 8 ? $", +{emptyResponses.Count - 8} more" : "";
			AnsiConsole.MarkupLine($"  [yellow]{emptyResponses.Count} empty[/] (Webull returned no bars — contract likely expired before chart history starts): {Markup.Escape(preview)}{Markup.Escape(more)}");
		}
		if (failures.Count > 0)
		{
			AnsiConsole.MarkupLine($"  [red]{failures.Count} failed[/]");
			foreach (var (occ, reason) in failures.Take(10))
				AnsiConsole.MarkupLine($"    [red]{Markup.Escape(occ)}[/]: {Markup.Escape(reason)}");
			if (failures.Count > 10) AnsiConsole.MarkupLine($"    …and {failures.Count - 10} more");
		}

		return failures.Count == 0 ? 0 : 2;
	}

	/// <summary>Merges <paramref name="existing"/> and <paramref name="incoming"/> by timestamp. Bars
	/// in both are replaced by the incoming version — Webull may revise late-reporting bars (especially
	/// the in-progress minute), and the most-recent fetch is the authoritative read. Returns the merged
	/// list sorted oldest-first plus the count of timestamps that weren't in <paramref name="existing"/>
	/// (used to distinguish "merged something new" from "no-op rewrite").</summary>
	internal static (List<OptionMinuteBar> Merged, int NewMinutes) MergeByTimestamp(IReadOnlyList<OptionMinuteBar> existing, IReadOnlyList<OptionMinuteBar> incoming)
	{
		var byTs = new Dictionary<long, OptionMinuteBar>();
		foreach (var b in existing) byTs[b.Timestamp.ToUnixTimeSeconds()] = b;
		var existingTimestamps = new HashSet<long>(byTs.Keys);
		foreach (var b in incoming) byTs[b.Timestamp.ToUnixTimeSeconds()] = b;
		var merged = byTs.Values.OrderBy(b => b.Timestamp).ToList();
		var newMinutes = byTs.Keys.Count(t => !existingTimestamps.Contains(t));
		return (merged, newMinutes);
	}

	/// <summary>Reads a previously-written option CSV. Lines that don't parse cleanly are dropped
	/// silently — same tolerance as <see cref="WebullChartsClient.ParseOptionBarRow"/>. Returns an
	/// empty list when the file doesn't exist; the caller can then treat the merge as a fresh write.</summary>
	internal static List<OptionMinuteBar> ReadOptionCsv(string path)
	{
		var bars = new List<OptionMinuteBar>();
		if (!File.Exists(path)) return bars;
		var lines = File.ReadAllLines(path);
		// Skip header.
		for (var i = 1; i < lines.Length; i++)
		{
			var line = lines[i];
			if (string.IsNullOrWhiteSpace(line)) continue;
			var parts = line.Split(',');
			if (parts.Length < 6) continue;
			if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) continue;
			if (!decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var o)) continue;
			if (!decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var h)) continue;
			if (!decimal.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var l)) continue;
			if (!decimal.TryParse(parts[4], NumberStyles.Any, CultureInfo.InvariantCulture, out var c)) continue;
			long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
			decimal? iv = null;
			if (parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6]) && decimal.TryParse(parts[6], NumberStyles.Any, CultureInfo.InvariantCulture, out var ivParsed))
				iv = ivParsed;
			bars.Add(new OptionMinuteBar(ts, o, h, l, c, v, iv));
		}
		return bars;
	}

	/// <summary>Collects every OCC option symbol that has appeared in <c>ai-proposals.jsonl</c> or
	/// <c>orders.jsonl</c> and whose root matches <paramref name="ticker"/>. Used as the default filter
	/// for backfill — these are the contracts the bot has actually picked (or the user has manually
	/// traded), which is the data the backtest will replay. Returns an empty set if neither file
	/// exists yet (fresh install with no live runs).</summary>
	internal static HashSet<string> LoadTouchedSymbols(string ticker) =>
		LoadTouchedSymbolsFromPaths(
			Program.ResolvePath("data/ai-proposals.jsonl"),
			Program.ResolvePath(Program.OrdersPath),
			ticker);

	/// <summary>Path-injectable variant of <see cref="LoadTouchedSymbols(string)"/>. Production code goes
	/// through the no-args overload; tests pass their own tmp paths so they don't read the user's
	/// prod data and don't race against a running watch loop.</summary>
	internal static HashSet<string> LoadTouchedSymbolsFromPaths(string proposalsPath, string ordersPath, string ticker)
	{
		var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		CollectFromProposals(proposalsPath, ticker, touched);
		CollectFromOrders(ordersPath, ticker, touched);
		return touched;
	}

	/// <summary>Reads a file line-by-line tolerating concurrent writers. <c>File.ReadLines</c> opens with
	/// <c>FileShare.Read</c>, which throws <c>IOException</c> when another process holds the file open
	/// for write — that's exactly what <c>wa ai watch</c> does to <c>ai-proposals.jsonl</c> while the
	/// market's open. Using <c>FileShare.ReadWrite</c> lets us share the handle with the writer; the
	/// last line we read may be partial but our JSON-parse-or-skip loop handles that gracefully.</summary>
	private static IEnumerable<string> ReadLinesShared(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream);
		string? line;
		while ((line = reader.ReadLine()) != null)
			yield return line;
	}

	/// <summary>Walks ai-proposals.jsonl line-by-line, parses each proposal's <c>legs[].symbol</c>, and
	/// adds OCC symbols whose root matches the ticker. Tolerant of malformed lines (e.g. partial writes
	/// from a crashed watch loop) — we skip them silently rather than failing the whole backfill.</summary>
	private static void CollectFromProposals(string path, string ticker, HashSet<string> touched)
	{
		if (!File.Exists(path)) return;
		foreach (var line in ReadLinesShared(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			JsonDocument? doc = null;
			try { doc = JsonDocument.Parse(line); }
			catch (JsonException) { continue; }
			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array) continue;
				foreach (var leg in legs.EnumerateArray())
				{
					if (!leg.TryGetProperty("symbol", out var symEl)) continue;
					var sym = symEl.GetString();
					if (string.IsNullOrWhiteSpace(sym)) continue;
					var parsed = ParsingHelpers.ParseOptionSymbol(sym!);
					if (parsed == null) continue;
					if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
					touched.Add(sym!);
				}
			}
		}
	}

	/// <summary>Walks orders.jsonl — Webull's orderList responses, one JSON envelope per line. Each
	/// envelope contains an <c>orderList</c> array of fills; we extract option fills whose root matches.
	/// Webull's order schema reports the OCC root in <c>symbol</c> and the contract description in
	/// <c>subSymbol</c> (e.g. "29 May 26 Call 100"); we need the canonical OCC, which lives in <c>ticker</c>
	/// for option fills but is sometimes empty. When we can't reconstruct the OCC reliably, we skip the
	/// fill — the proposals file is the authoritative source for opener legs anyway.</summary>
	private static void CollectFromOrders(string path, string ticker, HashSet<string> touched)
	{
		if (!File.Exists(path)) return;
		foreach (var line in ReadLinesShared(path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			JsonDocument? doc = null;
			try { doc = JsonDocument.Parse(line); }
			catch (JsonException) { continue; }
			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("orderList", out var orders) || orders.ValueKind != JsonValueKind.Array) continue;
				foreach (var order in orders.EnumerateArray())
				{
					// Build a canonical OCC symbol from (symbol, subSymbol). symbol = "TICKER $STRIKE"
					// (e.g. "GME $26.50"); subSymbol = "29 May 26 Call 100" (DD MMM YY {Call|Put} 100).
					// This is the only OCC-reconstruction surface — order fills don't ship the bare OCC.
					var occ = TryReconstructOcc(order, ticker);
					if (occ == null) continue;
					touched.Add(occ);
				}
			}
		}
	}

	private static string? TryReconstructOcc(JsonElement order, string ticker)
	{
		if (!order.TryGetProperty("tickerType", out var tt) || !string.Equals(tt.GetString(), "OPTION", StringComparison.OrdinalIgnoreCase)) return null;
		if (!order.TryGetProperty("symbol", out var symEl)) return null;
		if (!order.TryGetProperty("subSymbol", out var subEl)) return null;
		var symbol = symEl.GetString();
		var subSymbol = subEl.GetString();
		if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(subSymbol)) return null;

		// "GME $26.50" → root="GME", strike=26.50
		var dollarIdx = symbol.IndexOf('$');
		if (dollarIdx < 1) return null;
		var rootCandidate = symbol[..dollarIdx].TrimEnd();
		if (!string.Equals(rootCandidate, ticker, StringComparison.OrdinalIgnoreCase)) return null;
		if (!decimal.TryParse(symbol[(dollarIdx + 1)..].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) return null;

		// "29 May 26 Call 100" → date=2026-05-29, callPut=C
		var parts = subSymbol.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 4) return null;
		var dayStr = parts[0];
		var monStr = parts[1];
		var yearStr = parts[2];
		var typeStr = parts[3];
		// Normalize 2-digit year ("26" → "2026"). Webull always emits 2-digit years here.
		if (yearStr.Length == 2 && int.TryParse(yearStr, out var yy)) yearStr = (2000 + yy).ToString(CultureInfo.InvariantCulture);
		if (!DateTime.TryParseExact($"{dayStr} {monStr} {yearStr}", new[] { "d MMM yyyy", "dd MMM yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) return null;
		var cp = typeStr.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? "C" : typeStr.StartsWith("P", StringComparison.OrdinalIgnoreCase) ? "P" : null;
		if (cp == null) return null;

		// Strike is encoded as integer thousandths in OCC (e.g. 26.50 → "00026500").
		var strikeInt = (long)Math.Round(strike * 1000m);
		return $"{rootCandidate.ToUpperInvariant()}{expiry:yyMMdd}{cp}{strikeInt:D8}";
	}

	/// <summary>Builds the canonical CSV path for a contract: <c>data/options/&lt;ROOT&gt;/&lt;yyyy-MM-dd&gt;/&lt;OCC&gt;.csv</c>.
	/// Per-expiry grouping is a deliberate convention so a single <c>rm -rf data/options/SPXW/2026-05-26</c>
	/// cleanly drops one expiration's data without touching others — useful when re-running a backfill
	/// after a Webull schema change for example.</summary>
	internal static string BuildCsvPath(string rootDir, OptionParsed parsed, string occ)
	{
		var expiryDir = parsed.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		return Path.Combine(rootDir, expiryDir, occ + ".csv");
	}

	/// <summary>Writes the option-contract bars to CSV with the schema
	/// <c>timestamp_utc,open,high,low,close,volume,iv</c>. IV is blank when missing rather than "null"
	/// or "0" — the reader uses null to distinguish "endpoint reported no IV" from "IV is zero".</summary>
	internal static void WriteOptionCsv(string path, IReadOnlyList<OptionMinuteBar> bars)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var sb = new StringBuilder("timestamp_utc,open,high,low,close,volume,iv\n");
		foreach (var b in bars)
		{
			sb.Append(b.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Open.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.High.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Low.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Close.ToString(CultureInfo.InvariantCulture)).Append(',');
			sb.Append(b.Volume.ToString(CultureInfo.InvariantCulture)).Append(',');
			if (b.ImpliedVolatility.HasValue)
				sb.Append(b.ImpliedVolatility.Value.ToString(CultureInfo.InvariantCulture));
			sb.Append('\n');
		}
		var tmp = path + ".tmp";
		File.WriteAllText(tmp, sb.ToString());
		File.Move(tmp, path, overwrite: true);
	}

	/// <summary>Reports how many <paramref name="ticker"/> contracts the bot has touched (proposed or
	/// traded) that don't yet have a CSV on disk. Mirrors the default filter in <see cref="RunAsync"/>
	/// so the hint count matches what <c>--options</c> would actually backfill — without this, the
	/// hint would say "20114 pending" forever even after backfilling every contract the bot has ever
	/// picked. Called at the end of the regular `wa ai history &lt;ticker&gt;` flow.</summary>
	public static int CountUnbackfilledContracts(string ticker)
	{
		var registry = DerivativeIdRegistry.Snapshot();
		if (registry.Count == 0) return 0;
		var touched = LoadTouchedSymbols(ticker);
		if (touched.Count == 0) return 0;
		var optionsRoot = Path.Combine(Program.ResolvePath("data/options"), ticker.ToUpperInvariant());
		var pending = 0;
		foreach (var occ in touched)
		{
			if (!registry.ContainsKey(occ)) continue;
			var parsed = ParsingHelpers.ParseOptionSymbol(occ);
			if (parsed == null) continue;
			if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
			if (File.Exists(BuildCsvPath(optionsRoot, parsed, occ))) continue;
			pending++;
		}
		return pending;
	}
}
