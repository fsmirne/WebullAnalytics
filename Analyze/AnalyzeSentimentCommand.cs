using Spectre.Console;
using Spectre.Console.Cli;
using System.Globalization;
using WebullAnalytics.Sentiment;
using WebullAnalytics.Utils;

namespace WebullAnalytics.Analyze;

/// <summary>
/// <c>wa analyze sentiment</c> — Renders CNN's Fear & Greed Index for the most recent (or specified)
/// trading day. Shows the composite gauge, the seven sub-components with their raw market readings,
/// historical context (previous close / 1 week / 1 month / 1 year), and a contrarian interpretation
/// block that ties the regime to favored/dampened trade structures.
/// </summary>
internal sealed class AnalyzeSentimentSettings : AnalyzeBaseSettings
{
}

internal sealed class AnalyzeSentimentCommand : AsyncCommand<AnalyzeSentimentSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, AnalyzeSentimentSettings settings, CancellationToken cancellation)
	{
		var appConfig = Program.LoadAppConfig("report");
		if (appConfig != null) settings.ApplyConfig(appConfig);
		TerminalHelper.EnsureTerminalWidthFromConfig();

		var asOf = settings.EvaluationDateOverride ?? DateTime.Now;
		var snapshot = await FearGreedClient.FetchAsync(asOf, cancellation);
		if (snapshot == null)
		{
			AnsiConsole.MarkupLine("[red]Failed to fetch Fear & Greed data from CNN.[/] Network or upstream issue.");
			return 1;
		}

		RenderHeader(snapshot, asOf);
		AnsiConsole.WriteLine();
		RenderHistorical(snapshot);
		AnsiConsole.WriteLine();
		RenderComponents(snapshot);
		AnsiConsole.WriteLine();
		RenderInterpretation(snapshot);
		return 0;
	}

	private static void RenderHeader(SentimentSnapshot s, DateTime asOf)
	{
		var color = ColorFor(s.Score);
		var bar = BuildGauge(s.Score, width: 40);
		AnsiConsole.MarkupLine($"[bold]CNN Fear & Greed Index[/]  asof {s.Timestamp:yyyy-MM-dd HH:mm} UTC  [dim](query date {asOf:yyyy-MM-dd})[/]");
		AnsiConsole.MarkupLine($"  [bold {color}]{s.Score.ToString("F1", CultureInfo.InvariantCulture)}/100[/]  [italic]{Markup.Escape(s.Rating)}[/]");
		AnsiConsole.MarkupLine("  " + bar);
	}

	private static void RenderHistorical(SentimentSnapshot s)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Historical[/]");
		table.AddColumn(new TableColumn("Period").NoWrap());
		table.AddColumn(new TableColumn("Score").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Δ vs now").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Rating").NoWrap());

		AddHistoricalRow(table, "Now", s.Score, deltaVsNow: 0m, isCurrent: true);
		AddHistoricalRowOpt(table, "Previous close", s.PreviousClose, s.Score);
		AddHistoricalRowOpt(table, "1 week ago", s.Previous1Week, s.Score);
		AddHistoricalRowOpt(table, "1 month ago", s.Previous1Month, s.Score);
		AddHistoricalRowOpt(table, "1 year ago", s.Previous1Year, s.Score);

		AnsiConsole.Write(table);
	}

	private static void AddHistoricalRow(Table table, string label, decimal score, decimal deltaVsNow, bool isCurrent)
	{
		var color = ColorFor(score);
		var rating = SentimentRating.FromScore(score);
		var deltaStr = isCurrent
			? "[dim]—[/]"
			: $"[bold {(deltaVsNow >= 0m ? "green" : "red")}]{deltaVsNow.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}[/]";
		table.AddRow(label, $"[bold {color}]{score.ToString("F1", CultureInfo.InvariantCulture)}[/]", deltaStr, $"[italic]{Markup.Escape(rating)}[/]");
	}

	private static void AddHistoricalRowOpt(Table table, string label, decimal? prior, decimal current)
	{
		if (!prior.HasValue) { table.AddRow(label, "[dim]—[/]", "[dim]—[/]", "[dim]—[/]"); return; }
		var delta = current - prior.Value;
		AddHistoricalRow(table, label, prior.Value, deltaVsNow: delta, isCurrent: false);
	}

	private static void RenderComponents(SentimentSnapshot s)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).Title("[bold]Components[/]");
		table.AddColumn(new TableColumn("Indicator"));
		table.AddColumn(new TableColumn("Raw").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Score").RightAligned().NoWrap());
		table.AddColumn(new TableColumn("Rating").NoWrap());
		table.AddColumn(new TableColumn("Gauge").NoWrap());

		foreach (var c in s.Components)
		{
			var color = ColorFor(c.Score);
			var raw = c.RawValue.HasValue ? FormatRaw(c.Key, c.RawValue.Value) : "[dim]—[/]";
			table.AddRow(
				Markup.Escape(c.Label),
				raw,
				$"[bold {color}]{c.Score.ToString("F1", CultureInfo.InvariantCulture)}[/]",
				$"[italic]{Markup.Escape(c.Rating)}[/]",
				BuildGauge(c.Score, width: 18));
		}

		AnsiConsole.Write(table);
		AnsiConsole.MarkupLine("[dim]Each indicator is normalized 0–100 by CNN; raw value is the underlying market reading. Rating bands: 0–24 extreme fear · 25–49 fear · 50 neutral · 51–74 greed · 75–100 extreme greed.[/]");
	}

	private static string FormatRaw(string key, decimal raw) => key switch
	{
		"market_momentum_sp500" or "market_momentum_sp125" => $"{raw.ToString("F2", CultureInfo.InvariantCulture)}",
		"put_call_options" => raw.ToString("F2", CultureInfo.InvariantCulture),
		"market_volatility_vix" or "market_volatility_vix_50" => raw.ToString("F2", CultureInfo.InvariantCulture),
		"junk_bond_demand" => $"{raw.ToString("F2", CultureInfo.InvariantCulture)}%",
		"safe_haven_demand" => $"{raw.ToString("F2", CultureInfo.InvariantCulture)}%",
		"stock_price_strength" => $"{raw.ToString("F2", CultureInfo.InvariantCulture)}%",
		_ => raw.ToString("F2", CultureInfo.InvariantCulture),
	};

	/// <summary>Renders the contrarian interpretation: which structures the regime favors and which it
	/// dampens, plus a 1-week regime-change note when the index has shifted ≥10 points. The trade-side
	/// implications mirror what <see cref="WebullAnalytics.AI.CandidateScorer.ComputeSentimentFactor"/>
	/// applies to scoring, expressed in plain English.</summary>
	private static void RenderInterpretation(SentimentSnapshot s)
	{
		var lines = new List<string>();
		var color = ColorFor(s.Score);
		string headline;
		string contrarian;
		string vol;
		if (s.Score >= 75m)
		{
			headline = "Crowded long — late-cycle euphoria.";
			contrarian = "Favor: bearish/mean-reversion structures (long puts, bear verticals, short call spreads), neutral premium-sell setups (iron condors). Dampen: long calls, bull verticals.";
			vol = "Vol typically suppressed → debit structures (long calls/puts) price richer; protective puts cheaper. Premium-selling has shrinking edge.";
		}
		else if (s.Score >= 50m)
		{
			headline = "Greed regime — directional consensus tilts bullish.";
			contrarian = "Mild dampening of crowded longs; structures with bearish or neutral fits get a small boost. Most days the factor is near 1.0 — the overlay only moves the needle when the index reaches an extreme.";
			vol = "Vol moderate. No strong edge from sentiment alone — defer to per-ticker signal (technical bias, IV/HV, GEX).";
		}
		else if (s.Score >= 25m)
		{
			headline = "Fear regime — directional consensus tilts bearish.";
			contrarian = "Mild dampening of crowded shorts; bullish/neutral fits get a small boost. Mostly a no-op until the index drops further.";
			vol = "Vol moderate-to-elevated. Selective premium-selling on quality names finds better fills.";
		}
		else
		{
			headline = "Crowded short — capitulation territory.";
			contrarian = "Favor: bullish/mean-reversion structures (long calls, bull verticals, short put spreads), volatility-buying (long calendars / diagonals). Dampen: long puts, bear verticals, naked short calls.";
			vol = "Vol typically elevated → premium-selling has tailwind; debit structures expensive but fast IV-crush works for short-vol entries timed to a bottom.";
		}
		lines.Add($"[bold {color}]Regime:[/] {Markup.Escape(headline)}");
		lines.Add($"[bold]Trade-side implications:[/] {Markup.Escape(contrarian)}");
		lines.Add($"[bold]Volatility note:[/] {Markup.Escape(vol)}");

		if (s.Delta1Week is decimal dw && Math.Abs(dw) >= 10m)
		{
			var sign = dw >= 0m ? "+" : "−";
			var dir = dw >= 0m ? "greedier" : "more fearful";
			lines.Add($"[bold yellow]1-week shift:[/] {sign}{Math.Abs(dw).ToString("F0", CultureInfo.InvariantCulture)} pts ({dir}). Regime is in motion — vol/momentum readings from a week ago are stale.");
		}

		lines.Add("[dim]Macro overlay only. Single-name catalysts (earnings, FDA, M&A) routinely override the F&G regime on a given ticker.[/]");

		var panel = new Panel(string.Join("\n", lines)).Header("[bold]Interpretation[/]").Expand().Border(BoxBorder.Rounded).BorderColor(Color.Grey);
		AnsiConsole.Write(panel);
	}

	private static string ColorFor(decimal score) => score switch
	{
		<= 24m => "red",
		< 50m => "yellow",
		<= 50m => "grey85",
		< 75m => "green",
		_ => "lime",
	};

	/// <summary>Solid-block bar with a vertical "tick" marker at the score position. Width is the bar
	/// length in cells; the marker steals one cell at the position floor((score/100) × (width − 1)).</summary>
	private static string BuildGauge(decimal score, int width)
	{
		var clamped = (double)Math.Clamp(score, 0m, 100m);
		var pos = (int)Math.Round((clamped / 100.0) * (width - 1));
		var sb = new System.Text.StringBuilder();
		for (var i = 0; i < width; i++)
		{
			var color = ColorFor((decimal)((i / (double)(width - 1)) * 100.0));
			if (i == pos) sb.Append("[bold white on " + color + "]│[/]");
			else sb.Append("[" + color + "]█[/]");
		}
		return sb.ToString();
	}
}
