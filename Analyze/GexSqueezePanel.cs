using Spectre.Console;

namespace WebullAnalytics.Analyze;

/// <summary>Terminal rendering of a <see cref="SqueezeReading"/>: header with bias, probability bar with band labels,
/// per-factor bars, key levels and the setup checklist.</summary>
internal static class GexSqueezePanel
{
	private const int ScoreBarWidth = 60;
	private const int FactorBarWidth = 40;

	public static void Render(string ticker, DateTime asOf, string scope, SqueezeReading reading)
	{
		var bull = reading.Side == SqueezeSide.Bullish;
		var sideColor = bull ? "green" : "red";
		var sideWord = bull ? "Bullish" : "Bearish";
		var bandColor = BandColor(reading.Band);

		var grid = new Grid().Expand().AddColumn(new GridColumn().NoWrap()).AddColumn(new GridColumn().RightAligned().NoWrap());
		grid.AddRow(new Markup($"[bold]Gamma Squeeze Screener[/] · {Markup.Escape(ticker)}"), new Markup($"[bold {sideColor} on grey15] {sideWord.ToUpperInvariant()} BIAS [/]"));
		grid.AddRow(new Markup($"[dim]{asOf.ToString(asOf.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm")} · {Markup.Escape(scope)}[/]"), new Text(""));
		grid.AddEmptyRow();
		grid.AddRow(new Markup($"[bold {sideColor}]⚡ {sideWord} Squeeze[/]"), new Markup($"[bold {bandColor}]{reading.Band.ToUpperInvariant()}[/]"));
		grid.AddEmptyRow();
		grid.AddRow(new Markup("[bold grey]PROBABILITY SCORE[/]"), new Markup($"[bold {bandColor}]{reading.Score}[/][bold]/100[/]"));

		var rows = new List<Spectre.Console.Rendering.IRenderable>
		{
			grid,
			new Markup(ScoreBar(reading.Score)),
			new Markup($"[dim]{BandLabels()}[/]"),
			new Text(""),
			new Markup("[bold grey]FACTOR BREAKDOWN[/]"),
			FactorTable(reading.Factors),
			new Text(""),
			new Panel(KeyLevels(reading)).Header("[bold grey]KEY LEVELS[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey).Expand(),
			new Text(""),
			new Markup("[bold grey]SETUP ANALYSIS[/]"),
		};
		foreach (var f in reading.Factors)
			rows.Add(new Markup($"{MarkGlyph(f.Mark)} {(f.Mark == SetupMark.Unknown ? "[dim]" : "")}{Markup.Escape(f.Note)}{(f.Mark == SetupMark.Unknown ? "[/]" : "")}"));

		AnsiConsole.Write(new Panel(new Rows(rows)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(2, 1));
		var missing = reading.Factors.Where(f => !f.Points.HasValue).Select(f => f.Name).ToList();
		var excluded = missing.Count > 0 ? $"; excluded for lack of data: {string.Join(", ", missing)}" : "";
		AnsiConsole.MarkupLine($"[dim]Weights follow the third-party panel (25/25/25/20/5); the thresholds are ours. Score = points ÷ max over the factors with data{Markup.Escape(excluded)}. Both sides are scored and the bias is the higher one. Trigger = chain-wide gamma flip; walls = the strikes with the most call/put dollar gamma summed over the window. Volume is unsigned (which side traded, not who bought), and GEX terrain has tested as a RANGE signal, not a direction signal.[/]");
	}

	private static Table FactorTable(IReadOnlyList<SqueezeFactor> factors)
	{
		var table = new Table().NoBorder().HideHeaders().AddColumn(new TableColumn("").NoWrap()).AddColumn(new TableColumn("").NoWrap()).AddColumn(new TableColumn("").RightAligned().NoWrap());
		foreach (var f in factors)
		{
			var bar = f.Points.HasValue ? FactorBar(f.Points.Value, f.Max) : $"[grey23]{new string('━', FactorBarWidth)}[/]";
			var score = f.Points.HasValue ? $"{f.Points.Value}/{f.Max}" : $"[dim]n/a/{f.Max}[/]";
			table.AddRow(new Markup(Markup.Escape(f.Name)), new Markup(bar), new Markup(score));
		}
		return table;
	}

	private static Grid KeyLevels(SqueezeReading reading)
	{
		var t = reading.Terrain;
		var grid = new Grid().Expand().AddColumn(new GridColumn().NoWrap()).AddColumn(new GridColumn().RightAligned().NoWrap());
		grid.AddRow(new Markup("[grey]Current Price[/]"), new Markup($"[bold]${t.Spot:N2}[/]"));
		var wallLabel = reading.Side == SqueezeSide.Bullish ? "Call Wall" : "Put Wall";
		grid.AddRow(new Markup($"[grey]{wallLabel}[/]"), new Markup(reading.Wall.HasValue ? $"{PctTag(reading.Wall.Value, t.Spot)} [bold]${reading.Wall.Value:N2}[/]" : "[dim]—[/]"));
		grid.AddRow(new Markup("[grey]Trigger Level[/]"), new Markup(t.Trigger.HasValue ? $"{PctTag(t.Trigger.Value, t.Spot)} [bold yellow]${t.Trigger.Value:N2}[/]" : "[dim]— (no gamma flip in range)[/]"));
		if (t.DailyMove.HasValue) grid.AddRow(new Markup("[grey]Daily Expected Move[/]"), new Markup($"[dim]±${t.DailyMove.Value:N2} ({t.DailyMove.Value / t.Spot:P2})[/]"));
		return grid;
	}

	private static string PctTag(decimal level, decimal spot)
	{
		var pct = (level - spot) / spot;
		return $"[{(pct >= 0m ? "green" : "red")}]({pct:+0.00%;-0.00%})[/]";
	}

	/// <summary>Score bar with tick marks at the band boundaries (30 / 50 / 75).</summary>
	private static string ScoreBar(int score)
	{
		var filled = (int)Math.Round(ScoreBarWidth * Math.Clamp(score, 0, 100) / 100m, MidpointRounding.AwayFromZero);
		var ticks = new[] { 30, 50, 75 }.Select(b => b * ScoreBarWidth / 100).ToHashSet();
		var sb = new System.Text.StringBuilder();
		for (var i = 0; i < ScoreBarWidth; i++)
		{
			var on = i < filled;
			var glyph = ticks.Contains(i) ? '┃' : '━';
			sb.Append(on ? $"[blue]{glyph}[/]" : $"[grey23]{glyph}[/]");
		}
		return sb.ToString();
	}

	private static string BandLabels()
	{
		var line = new char[ScoreBarWidth];
		Array.Fill(line, ' ');
		Place(line, 0, "Unlikely");
		Place(line, 30 * ScoreBarWidth / 100, "Possible");
		Place(line, 50 * ScoreBarWidth / 100, "Likely");
		Place(line, ScoreBarWidth - "Imminent".Length, "Imminent");
		return new string(line);
	}

	private static void Place(char[] line, int at, string label) => label.CopyTo(0, line, at, label.Length);

	private static string FactorBar(int points, int max)
	{
		var filled = max > 0 ? (int)Math.Round(FactorBarWidth * (decimal)points / max, MidpointRounding.AwayFromZero) : 0;
		var share = max > 0 ? (decimal)points / max : 0m;
		var color = share >= 0.7m ? "green" : share >= 0.3m ? "yellow" : "red";
		return $"[{color}]{new string('━', filled)}[/][grey23]{new string('━', FactorBarWidth - filled)}[/]";
	}

	private static string MarkGlyph(SetupMark mark) => mark switch
	{
		SetupMark.Pass => "[green]✔[/]",
		SetupMark.Warn => "[yellow]⚠[/]",
		SetupMark.Fail => "[red]✘[/]",
		_ => "[dim]·[/]",
	};

	private static string BandColor(string band) => band switch
	{
		"Imminent" => "red",
		"Likely" => "blue",
		"Possible" => "yellow",
		_ => "grey",
	};
}
