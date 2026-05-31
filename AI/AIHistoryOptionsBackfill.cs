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
/// <para>Idempotent re-runs: Webull (live) contracts merge by timestamp so a re-run picks up new
/// minutes. Massive (expired) contracts that already have a CSV and whose expiry is in the past are
/// skipped entirely — their minute history is final, so re-fetching wastes the rate-limited budget.
/// <c>--force</c> overrides both: drop and refetch from scratch.</para></summary>
/// <summary>Which upstream serves a given contract's per-minute bars. Webull is fast and unrestricted
/// but only works while the contract is live (<see cref="DerivativeIdRegistry"/> resolves OCC → id
/// at chain-fetch time and the resolution is lost after expiry). Massive (Polygon mirror) serves
/// expired contracts but is rate-limited to 5 req/min at the basic tier and ships no IV. Each
/// contract picks one source for its lifetime in the cache.</summary>
internal enum OptionDataSource { Webull, Massive }

internal static class AIHistoryOptionsBackfill
{
	private static readonly TimeSpan DefaultPace = TimeSpan.FromMilliseconds(200);
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	public static async Task<int> RunAsync(string ticker, bool force, bool all, DateTime? since, int webullPad, CancellationToken cancellation)
	{
		AnsiConsole.MarkupLine($"[bold]Option backfill for {Markup.Escape(ticker)}[/]" + (since.HasValue ? $" (expiry ≥ {since.Value:yyyy-MM-dd})" : ""));

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
		// Pace the massive (expired-contract) path to the configured tier. Basic = 5/min; Options Starter+
		// is unlimited (set massiveMaxRequestsPerMinute to a large value or 0 to run at full speed).
		MassivePolygonClient.MaxRequestsPerWindow = apiConfig?.MassiveMaxRequestsPerMinute ?? 5;

		if (apiConfig == null || apiConfig.Headers.Count == 0)
		{
			AnsiConsole.MarkupLine("  [red]api-config.json has no headers[/] — run `wa sniff` to refresh");
			return 1;
		}

		var registry = DerivativeIdRegistry.Snapshot();

		// Default mode collects touched OCCs from proposals/orders/discovery, then routes each one
		// to Webull (live, in registry) or massive (expired, not in registry). --all keeps the legacy
		// behavior of pulling every Webull-registry entry for this ticker — useful for ingesting the
		// full live chain, but most strikes are illiquid wings the strategy never picks.
		List<(string Occ, long? DerivativeId, OptionParsed Parsed)> matches;
		if (all)
		{
			matches = new();
			foreach (var (occ, id) in registry)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(occ);
				if (parsed == null) continue;
				if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
				matches.Add((occ, id, parsed));
			}
			AnsiConsole.MarkupLine($"  --all: keeping {matches.Count} contract(s) from full registry");
		}
		else
		{
			var touched = LoadTouchedSymbols(ticker);
			matches = new(touched.Count);
			foreach (var occ in touched)
			{
				var parsed = ParsingHelpers.ParseOptionSymbol(occ);
				if (parsed == null) continue;
				if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
				registry.TryGetValue(occ, out var id);
				matches.Add((occ, id > 0 ? id : null, parsed));
			}
			AnsiConsole.MarkupLine($"  filter: keeping {matches.Count} contract(s) from proposals/orders/discovery (use --all to backfill the full live chain)");
		}

		// Webull-side pad: widen each Webull-routable (expiry, right) by N strikes beyond the touched
		// range. Webull's chart endpoint is unrestricted (5 req/sec) so this is essentially free, and
		// it lets future strategy variants (wider delta bands, deeper wings) replay against real data
		// instead of synthetic — historical bars become inaccessible once the contract drops out of
		// the live chain. Massive-routable contracts (expired, not in registry) are intentionally
		// excluded so we don't bloat the rate-limited (5 req/min) path with strikes the strategy
		// never picked. Skipped under --all (already pulls everything) and when webullPad == 0.
		if (!all && webullPad > 0 && matches.Count > 0)
		{
			// Step 1: collect touched ranges per (expiry, right), considering only Webull-routable
			// anchors. A touched OCC without a derivativeId is a Massive contract — padding around it
			// via Webull is impossible (the contract isn't in the live chain), so it can't anchor.
			var touchedRanges = new Dictionary<(DateTime Expiry, string Right), (decimal Min, decimal Max)>();
			foreach (var (_, id, p) in matches)
			{
				if (!id.HasValue || id.Value <= 0) continue;
				var key = (p.ExpiryDate.Date, p.CallPut);
				if (!touchedRanges.TryGetValue(key, out var range))
					touchedRanges[key] = (p.Strike, p.Strike);
				else if (p.Strike < range.Min || p.Strike > range.Max)
					touchedRanges[key] = (Math.Min(range.Min, p.Strike), Math.Max(range.Max, p.Strike));
			}

			// Step 2: mirror-side fallback. If an expiry has touched picks in C but none in P (or vice
			// versa), copy the touched side's range to the opposite right — a strategy variant that
			// trades puts at this expiry would almost certainly use similar strike distances as calls.
			// Skips expiries with zero touched picks on either side (per the agreed (a) policy:
			// strategy-variant headroom for enabled DTEs only).
			var toMirror = new List<(DateTime Expiry, string Right, decimal Min, decimal Max)>();
			foreach (var kv in touchedRanges)
			{
				var oppositeRight = kv.Key.Right == "C" ? "P" : "C";
				var oppositeKey = (kv.Key.Expiry, oppositeRight);
				if (!touchedRanges.ContainsKey(oppositeKey))
					toMirror.Add((kv.Key.Expiry, oppositeRight, kv.Value.Min, kv.Value.Max));
			}
			foreach (var (exp, right, min, max) in toMirror)
				touchedRanges[(exp, right)] = (min, max);

			// Step 3: pre-index the registry by (expiry, right) → strike-sorted list. Built once so
			// the per-(expiry, right) lookup below is O(log N) bisect + O(pad) slice instead of an
			// O(registry) scan per anchor group.
			var registryByExpiryRight = new Dictionary<(DateTime Expiry, string Right), List<(decimal Strike, string Occ, long Id)>>();
			foreach (var (occ, id) in registry)
			{
				if (id <= 0) continue;
				var p = ParsingHelpers.ParseOptionSymbol(occ);
				if (p == null) continue;
				if (!string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
				var key = (p.ExpiryDate.Date, p.CallPut);
				if (!registryByExpiryRight.TryGetValue(key, out var list))
					registryByExpiryRight[key] = list = new List<(decimal, string, long)>();
				list.Add((p.Strike, occ, id));
			}
			foreach (var list in registryByExpiryRight.Values)
				list.Sort((a, b) => a.Strike.CompareTo(b.Strike));

			// Step 4: for each anchor (expiry, right), slice the registry to include every strike from
			// the touched min..max (interior gap fill) plus N strikes outside each end. Already-matched
			// OCCs are deduped via alreadyMatched so the inner padding doesn't re-add touched picks.
			var alreadyMatched = new HashSet<string>(matches.Select(m => m.Occ), StringComparer.OrdinalIgnoreCase);
			var added = 0;
			foreach (var kv in touchedRanges)
			{
				if (!registryByExpiryRight.TryGetValue(kv.Key, out var registryStrikes)) continue;
				var min = kv.Value.Min;
				var max = kv.Value.Max;
				// Find the slice bounds: first registry index with strike >= min, last index with
				// strike <= max. Then widen by webullPad strikes on each side, clamped to the list.
				var first = registryStrikes.FindIndex(e => e.Strike >= min);
				var last = registryStrikes.FindLastIndex(e => e.Strike <= max);
				if (first < 0 || last < 0) continue;
				var lo = Math.Max(0, first - webullPad);
				var hi = Math.Min(registryStrikes.Count - 1, last + webullPad);
				for (int i = lo; i <= hi; i++)
				{
					var entry = registryStrikes[i];
					if (!alreadyMatched.Add(entry.Occ)) continue;
					var parsed = ParsingHelpers.ParseOptionSymbol(entry.Occ);
					if (parsed == null) continue;
					matches.Add((entry.Occ, entry.Id, parsed));
					added++;
				}
			}

			AnsiConsole.MarkupLine($"  --webull-pad {webullPad}: added {added} Webull-routable contract(s) around the touched range" + (toMirror.Count > 0 ? $" (mirrored {toMirror.Count} (expiry, right) from the opposite side)" : ""));
		}

		// Bound to the requested expiry window (skips the rest of the catalog — e.g. validate 2026 YTD
		// fast, then run 2025 overnight). Applies to every mode.
		if (since.HasValue)
		{
			var before = matches.Count;
			matches = matches.Where(m => m.Parsed.ExpiryDate.Date >= since.Value.Date).ToList();
			AnsiConsole.MarkupLine($"  --since {since.Value:yyyy-MM-dd}: {matches.Count}/{before} contract(s) in window");
		}

		if (matches.Count == 0)
		{
			AnsiConsole.MarkupLine($"  [yellow]nothing to backfill for {Markup.Escape(ticker)}[/]");
			return 0;
		}

		var optionsRoot = Path.Combine(Program.ResolvePath("data/options"), ticker.ToUpperInvariant());

		// Classify each match: Webull (has derivativeId) vs Massive (needs OCC fallback). Massive requires
		// an apiKey — when missing we skip those contracts with a note instead of failing the run, so a
		// user without a massive subscription still gets the Webull half. Merge semantics: if the CSV
		// exists and --force isn't set, the fetched bars merge with what's on disk (preserves any captured
		// IV the existing file has, which Webull-source CSVs carry and massive-source CSVs don't).
		var work = new List<(string Occ, long? DerivativeId, OptionParsed Parsed, string CsvPath, bool MergeExisting, OptionDataSource Source)>();
		var skippedNoMassive = 0;
		var skippedComplete = 0;
		var hasMassiveKey = !string.IsNullOrWhiteSpace(apiConfig!.MassiveApiKey);
		var todayEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, NyTz).Date;
		foreach (var (occ, id, parsed) in matches)
		{
			var csvPath = BuildCsvPath(optionsRoot, parsed, occ);
			var csvExists = File.Exists(csvPath);
			var mergeExisting = !force && csvExists;
			if (id.HasValue)
			{
				// Once a contract is on disk we never need to re-pull it for backfill: every past trading
				// day's minute history is final, and a backtest only needs bars through its window end.
				// The sole exception is TODAY's expiry, whose intraday is still forming. Future-expiry
				// on-disk contracts are skipped too — their already-captured bars are what the backtest
				// uses; new bars print on future trading days outside any historical window. --force overrides.
				if (!force && csvExists && parsed.ExpiryDate.Date != todayEt)
				{
					skippedComplete++;
					continue;
				}
				work.Add((occ, id, parsed, csvPath, mergeExisting, OptionDataSource.Webull));
			}
			else if (hasMassiveKey)
			{
				// Same rule as the Webull route: if it's on disk, skip — except today's expiry (still
				// forming intraday). Past and future on-disk contracts are both final for backtest
				// purposes, so only genuinely-new (not-yet-on-disk) or today's contracts hit the endpoint.
				if (!force && csvExists && parsed.ExpiryDate.Date != todayEt)
				{
					skippedComplete++;
					continue;
				}
				work.Add((occ, null, parsed, csvPath, mergeExisting, OptionDataSource.Massive));
			}
			else
			{
				skippedNoMassive++;
			}
		}
		var webullCount = work.Count(w => w.Source == OptionDataSource.Webull);
		var massiveCount = work.Count(w => w.Source == OptionDataSource.Massive);
		AnsiConsole.MarkupLine($"  routing: {webullCount} via Webull (live), {massiveCount} via massive.com (expired)"
			+ (skippedComplete > 0 ? $", [green]{skippedComplete} already complete[/] (expired + on disk)" : "")
			+ (skippedNoMassive > 0 ? $", [yellow]{skippedNoMassive} skipped[/] (no MassiveApiKey)" : ""));

		// Split by route and run them CONCURRENTLY. Webull is rate-paced (sequential, DefaultPace between
		// calls) and was previously sorted first, which made the whole slow Webull pass complete before the
		// massive pass even started. Now the two routes overlap: Webull walks its list sequentially while
		// massive pulls in parallel (its own rolling-window throttle caps the rate — a no-op at the unlimited
		// tier, so the bounded parallelism actually applies). Each contract writes its own CSV, so concurrent
		// writes never touch the same file.
		var webullWork = work.Where(w => w.Source == OptionDataSource.Webull).OrderBy(w => w.Parsed.ExpiryDate).ThenBy(w => w.Parsed.Strike).ToList();
		var massiveWork = work.Where(w => w.Source == OptionDataSource.Massive).OrderBy(w => w.Parsed.ExpiryDate).ThenBy(w => w.Parsed.Strike).ToList();

		var written = 0;
		var merged = 0;
		var unchanged = 0;
		var processed = 0;
		var total = work.Count;
		var emptyResponses = new System.Collections.Concurrent.ConcurrentBag<string>();
		var failures = new System.Collections.Concurrent.ConcurrentBag<(string Occ, string Reason)>();
		var progressLock = new object();

		async Task ProcessOneAsync((string Occ, long? DerivativeId, OptionParsed Parsed, string CsvPath, bool MergeExisting, OptionDataSource Source) item, CancellationToken ct)
		{
			IReadOnlyList<OptionMinuteBar> bars;
			try
			{
				if (item.Source == OptionDataSource.Webull)
				{
					bars = await WebullChartsClient.FetchOptionContractMinuteBarsAsync(apiConfig!, item.DerivativeId!.Value, 800, anchorUnixSec: null, ct);
				}
				else
				{
					// Polygon coverage window: from 30 days before expiry through expiry. SPXW weeklies
					// list ~T-1 week so 30d is generous headroom; SPX monthlies list weeks earlier, also fine.
					var listFrom = DateOnly.FromDateTime(item.Parsed.ExpiryDate.AddDays(-30));
					var expireTo = DateOnly.FromDateTime(item.Parsed.ExpiryDate);
					bars = await MassivePolygonClient.FetchOptionMinuteAggregatesAsync(apiConfig!.MassiveApiKey, item.Occ, listFrom, expireTo, ct);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				failures.Add((item.Occ, ex.Message));
				return;
			}

			if (bars.Count == 0) emptyResponses.Add(item.Occ);
			else if (item.MergeExisting)
			{
				var existing = ReadOptionCsv(item.CsvPath);
				var (mergedBars, newMinutes) = MergeByTimestamp(existing, bars);
				if (newMinutes == 0) Interlocked.Increment(ref unchanged);
				else { WriteOptionCsv(item.CsvPath, mergedBars); Interlocked.Increment(ref merged); }
			}
			else { WriteOptionCsv(item.CsvPath, bars); Interlocked.Increment(ref written); }

			var done = Interlocked.Increment(ref processed);
			if (done % 25 == 0 || done == total)
				lock (progressLock)
					AnsiConsole.MarkupLine($"    progress: {done}/{total} (wrote {written}, merged {merged}, unchanged {unchanged}, empty {emptyResponses.Count}, failed {failures.Count})");
		}

		// Webull route: sequential with the client-side pace (the chart endpoint tolerates this rate reliably).
		var webullTask = Task.Run(async () =>
		{
			for (var i = 0; i < webullWork.Count; i++)
			{
				cancellation.ThrowIfCancellationRequested();
				if (i > 0) await Task.Delay(DefaultPace, cancellation);
				await ProcessOneAsync(webullWork[i], cancellation);
			}
		}, cancellation);

		// Massive route: bounded parallelism (its throttle enforces any tier cap; unlimited → true concurrency).
		var massiveTask = Parallel.ForEachAsync(massiveWork,
			new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation },
			async (item, ct) => await ProcessOneAsync(item, ct));

		await Task.WhenAll(webullTask, massiveTask);

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
			Path.Combine(Program.ResolvePath("data/options-discovery"), ticker.ToUpperInvariant() + ".jsonl"),
			ticker);

	/// <summary>Path-injectable variant of <see cref="LoadTouchedSymbols(string)"/>. Production code goes
	/// through the no-args overload; tests pass their own tmp paths so they don't read the user's
	/// prod data and don't race against a running watch loop.</summary>
	internal static HashSet<string> LoadTouchedSymbolsFromPaths(string proposalsPath, string ordersPath, string discoveryPath, string ticker)
	{
		var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		CollectFromProposals(proposalsPath, ticker, touched);
		CollectFromOrders(ordersPath, ticker, touched);
		CollectFromDiscovery(discoveryPath, ticker, touched);
		return touched;
	}

	/// <summary>Walks the per-ticker discovery log written by <c>wa ai backtest --discover</c> and adds
	/// any OCC whose root matches. The log holds the union of OCCs picked across all past backtest
	/// runs — a strategy-footprint catalog that grows as the user explores different windows.</summary>
	private static void CollectFromDiscovery(string path, string ticker, HashSet<string> touched)
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
				if (!doc.RootElement.TryGetProperty("occ", out var el)) continue;
				var occ = el.GetString();
				if (string.IsNullOrWhiteSpace(occ)) continue;
				var parsed = ParsingHelpers.ParseOptionSymbol(occ!);
				if (parsed == null) continue;
				if (!string.Equals(parsed.Root, ticker, StringComparison.OrdinalIgnoreCase)) continue;
				touched.Add(occ!);
			}
		}
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
