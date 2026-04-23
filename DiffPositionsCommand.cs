using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace WebullAnalytics;

internal sealed class DiffPositionsSettings : CommandSettings
{
	[CommandOption("--source")]
	[Description("Data source: 'api' (JSONL, default) or 'export' (Webull CSV exports)")]
	public string Source { get; set; } = "api";
}

/// <summary>
/// Runs both the legacy StrategyGrouper path and the new PositionReplay path against the same trade stream,
/// then renders a table of PositionRows where InitialAvgPrice or AdjustedAvgPrice differ. Used during Phase 2
/// validation of the PositionReplay rewrite; to be removed after Phase 3.
/// </summary>
internal sealed class DiffPositionsCommand : AsyncCommand<DiffPositionsSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, DiffPositionsSettings settings, CancellationToken cancellation)
	{
		// Load trades once (shared between both backends).
		var ordersPath = Program.ResolvePath(Program.OrdersPath);
		if (!File.Exists(ordersPath))
		{
			AnsiConsole.MarkupLine($"[red]Error:[/] Orders file '{Markup.Escape(ordersPath)}' does not exist.");
			return 2;
		}
		var (trades, feeLookup) = JsonlParser.ParseOrdersJsonl(ordersPath);
		var (_, positions, _) = PositionTracker.ComputeReport(trades, feeLookup: feeLookup);
		var tradeIndex = PositionTracker.BuildTradeIndex(trades);

		// Run legacy.
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", "legacy");
		var (legacyRows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);

		// Run replay.
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", "replay");
		var (replayRows, _, _) = PositionTracker.BuildPositionRows(positions, tradeIndex, trades);
		Environment.SetEnvironmentVariable("WA_ADJ_BASIS", null);

		// Build a key → row map for each. Key = Instrument + "|" + Expiry (matches visible display).
		string RowKey(PositionRow r) => $"{r.Instrument}|{r.Expiry?.ToString("yyyy-MM-dd") ?? "-"}|{r.Side}";
		var legacyByKey = legacyRows.Where(r => !r.IsStrategyLeg).GroupBy(RowKey).ToDictionary(g => g.Key, g => g.First());
		var replayByKey = replayRows.Where(r => !r.IsStrategyLeg).GroupBy(RowKey).ToDictionary(g => g.Key, g => g.First());
		var allKeys = legacyByKey.Keys.Union(replayByKey.Keys).OrderBy(k => k).ToList();

		var table = new Table().Title("[bold]Position adj-basis diff — legacy vs replay[/]");
		table.AddColumn("Position");
		table.AddColumn("Qty");
		table.AddColumn(new TableColumn("Legacy init").RightAligned());
		table.AddColumn(new TableColumn("Replay init").RightAligned());
		table.AddColumn(new TableColumn("Legacy adj").RightAligned());
		table.AddColumn(new TableColumn("Replay adj").RightAligned());
		table.AddColumn(new TableColumn("Δ adj").RightAligned());

		int matches = 0, diffs = 0, missingLeft = 0, missingRight = 0;
		foreach (var key in allKeys)
		{
			legacyByKey.TryGetValue(key, out var L);
			replayByKey.TryGetValue(key, out var R);
			if (L == null) { missingLeft++; table.AddRow(Markup.Escape(key), "-", "[red]missing[/]", "-", "-", R?.AdjustedAvgPrice?.ToString("F2") ?? "-", "-"); continue; }
			if (R == null) { missingRight++; table.AddRow(Markup.Escape(key), L.Qty.ToString(), L.InitialAvgPrice?.ToString("F2") ?? "-", "[red]missing[/]", L.AdjustedAvgPrice?.ToString("F2") ?? "-", "-", "-"); continue; }
			var initMatch = Math.Abs((L.InitialAvgPrice ?? 0m) - (R.InitialAvgPrice ?? 0m)) < 0.01m;
			var adjMatch = Math.Abs((L.AdjustedAvgPrice ?? 0m) - (R.AdjustedAvgPrice ?? 0m)) < 0.01m;
			if (initMatch && adjMatch) { matches++; continue; }
			diffs++;
			var delta = (R.AdjustedAvgPrice ?? 0m) - (L.AdjustedAvgPrice ?? 0m);
			table.AddRow(Markup.Escape(key), L.Qty.ToString(), L.InitialAvgPrice?.ToString("F2") ?? "-", R.InitialAvgPrice?.ToString("F2") ?? "-", L.AdjustedAvgPrice?.ToString("F2") ?? "-", R.AdjustedAvgPrice?.ToString("F2") ?? "-", (delta >= 0m ? "+" : "") + delta.ToString("F2"));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine($"[bold]Summary:[/] {matches} match, {diffs} diff, {missingLeft} only-in-replay, {missingRight} only-in-legacy.");
		await Task.CompletedTask;
		return 0;
	}
}
