using Spectre.Console;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Terminal rendering of a <see cref="CascadeReading"/>, styled after <see cref="GexSqueezePanel"/>:
/// a header with the classified regime badge and the 4-regime comparison row across the full width, then two side-by-side subpanels
/// (stacked on a terminal narrower than <see cref="SideBySideMinWidth"/>): where the setup stands (0–100 score bar, key levels &
/// telemetry) and why (5-factor breakdown, setup checklist). The watch loop adds a full-width trade subpanel at the bottom.
/// </summary>
internal static class CascadePanel
{
	private const int ScoreBarWidth = 60;
	private const int FactorBarWidth = 28;

	/// <summary>Terminals at least this wide get the two side-by-side subpanels; narrower ones stack them.</summary>
	private const int SideBySideMinWidth = 190;
	private const int NarrowFactorBarWidth = 16;
	/// <summary>A subpanel's border and padding: one column of each on either side.</summary>
	private const int SubPanelChrome = 4;

	/// <param name="trade">A full-width subpanel rendered at the bottom (the watch loop's trade section); null for none.</param>
	public static void Render(string ticker, DateTime asOf, string scope, CascadeReading reading, bool showLegend = true, Spectre.Console.Rendering.IRenderable? trade = null)
	{
		var lead = reading.LeadingSetup;
		var active = reading.Active;
		var regimeColor = RegimeColor(reading.PrimaryRegime, lead.Regime);
		var closed = reading.Terrain.SessionClosed;
		var badgeText = closed ? $" RECAP · {CascadeReading.RegimeLabel(lead.Regime)} "
			: active ? $" {CascadeReading.RegimeLabel(reading.PrimaryRegime)} "
			: $" NEUTRAL · LEAN {CascadeReading.RegimeLabel(lead.Regime)} ";

		var headerGrid = new Grid().Expand()
			.AddColumn(new GridColumn().NoWrap())
			.AddColumn(new GridColumn().RightAligned().NoWrap());
		headerGrid.AddRow(
			new Markup($"[bold]Cascade & Trap Screener[/] · {Markup.Escape(ticker)}"),
			new Markup($"[bold {regimeColor} on grey15]{Markup.Escape(badgeText)}[/]"));
		headerGrid.AddRow(
			new Markup($"[dim]{asOf.ToString(asOf.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm")} ET · {Markup.Escape(scope)}[/]"),
			new Text(""));
		headerGrid.AddEmptyRow();

		var headlineGlyph = reading.DirectionalSign > 0 ? "▲" : "▼";
		var statusLabel = closed ? "SESSION CLOSED"
			: active ? (lead.Score >= CascadeScreener.StrongScore ? "CONFIRMED · STRONG SCORE" : "CONFIRMED")
			: (lead.Complete ? "BELOW TRIGGER" : "UNCONFIRMED");
		headerGrid.AddRow(
			new Markup($"[bold {regimeColor}]{headlineGlyph} {Markup.Escape(CascadeReading.RegimeLabel(lead.Regime))}[/]"),
			new Markup($"[bold {regimeColor}]{statusLabel}[/]"));
		if (closed)
		{
			var closedMsg = reading.Inputs.Tape != null
				? "The session has closed: this is a recap of the whole day using data from the close (full-session flow and tape), and nothing confirms when the market is not open."
				: "The market is not open: this reading uses data from the previous close (no current-day session tape), and nothing confirms when the market is not open.";
			headerGrid.AddRow(
				new Markup($"[yellow]{closedMsg}{(reading.Terrain.FrontSettled ? " The front expiry has settled, so its gamma levels are not scored." : "")}[/]"),
				new Text(""));
		}

		var rows = new List<Spectre.Console.Rendering.IRenderable> { headerGrid, new Text(""), RegimeComparisonTable(reading) };
		// Inside the parent panel: its two border columns and its padding of 2 on each side.
		var inner = AnsiConsole.Profile.Width - 6;
		var sideBySide = AnsiConsole.Profile.Width >= SideBySideMinWidth;
		// Each subpanel is titled by its first section; its second section keeps its own heading inside.
		var leftContent = ScoreAndLevels(reading, lead, regimeColor, closed, expand: true);
		var rightContent = FactorsAndNotes(lead, sideBySide ? NarrowFactorBarWidth : FactorBarWidth);
		var left = SubPanel("SETUP SCORE", leftContent);
		var right = SubPanel($"FACTOR BREAKDOWN ({CascadeReading.RegimeLabel(lead.Regime)})", rightContent);
		if (sideBySide)
		{
			// The two columns and the one-column gap between them fill the inner width exactly. The left keeps the natural width of its
			// label/value rows (measured on a copy whose grids do not stretch); the right gets what its factor details and notes ask for,
			// at least half when the left can spare it, never under two fifths; the left takes the rest. A long right line then wraps
			// inside its own cell rather than a key level breaking across two rows.
			var options = Spectre.Console.Rendering.RenderOptions.Create(AnsiConsole.Console);
			var leftNeeds = ((Spectre.Console.Rendering.IRenderable)ScoreAndLevels(reading, lead, regimeColor, closed, expand: false)).Measure(options, inner).Max + SubPanelChrome;
			var rightWants = ((Spectre.Console.Rendering.IRenderable)rightContent).Measure(options, inner).Max + SubPanelChrome;
			var rightWidth = Math.Max((inner - 1) * 2 / 5, Math.Min(Math.Max(rightWants, (inner - 1) / 2), inner - 1 - leftNeeds));
			var columnWidth = inner - 1 - rightWidth;
			// Equal heights, so the two bottom borders line up.
			var height = Math.Max(HeightOf(left, columnWidth), HeightOf(right, rightWidth));
			left.Height = height;
			right.Height = height;
			var body = new Grid().AddColumn(new GridColumn().Width(columnWidth).PadLeft(0).PadRight(1)).AddColumn(new GridColumn().Width(rightWidth).PadLeft(0).PadRight(0));
			body.AddRow(left, right);
			rows.Add(body);
		}
		else
		{
			rows.Add(left);
			rows.Add(right);
		}
		if (trade != null) rows.Add(trade);

		AnsiConsole.Write(new Panel(new Rows(rows)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(2, 1).Expand());
		if (showLegend)
			AnsiConsole.MarkupLine("[dim]Regimes: [red]BEAR CASCADE[/] = short-γ + put flow + intraday VIX expansion + VWAP breakdown. [aqua]BEAR TRAP[/] = gap-down / morning dip + intraday VIX stall + VWAP reclaim. [green]BULL CASCADE[/] = intraday VIX crush + call flow + overhead target wall + VWAP breakout. [yellow]BULL TRAP[/] = gap-up / morning rally + VIX failing to crush + VWAP roll-over. Describes the session so far, not what comes next: over 2022–2026 a confirmed regime at 09:50 ET showed no edge from there to the close. 0DTE " + GexLegends.PullLegendMarkup + "[/]");
	}

	/// <summary>A titled subpanel of the cascade panel, in the house style.</summary>
	internal static Panel SubPanel(string title, Spectre.Console.Rendering.IRenderable content) =>
		new Panel(content).Header($"[bold grey]{Markup.Escape(title)}[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey).Expand();

	/// <summary>Where the setup stands: its score against the trigger, and the key levels.</summary>
	/// <param name="expand">Stretch the score row and the key levels to the subpanel's width; false gives their natural width, for sizing.</param>
	private static Rows ScoreAndLevels(CascadeReading reading, CascadeSetupEvaluation lead, string regimeColor, bool closed, bool expand)
	{
		var earned = lead.Factors.Sum(f => f.Points ?? 0);
		var possible = lead.Factors.Where(f => f.Points.HasValue).Sum(f => f.Max);
		var scoredCount = lead.Factors.Count(f => f.Points.HasValue);
		var coverage = $"{earned}/{possible} pts · {scoredCount} of {lead.Factors.Count} factors · trigger ≥{reading.TriggerScore}";
		// The subpanel's title names the score, so its first row is the score itself and how much of it was scored.
		var score = new Grid { Expand = expand }.AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn(new GridColumn().RightAligned());
		score.AddRow(new Markup($"[bold {regimeColor}]{lead.Score}[/][bold]/100[/]"), new Markup(closed ? $"[dim]{coverage} · recap[/]" : lead.Complete ? $"[dim]{coverage}[/]" : $"[yellow]{coverage} · awaiting confirmation gates[/]"));
		return new Rows(
			score,
			new Markup(ScoreBar(lead.Score, reading.TriggerScore, regimeColor)),
			new Markup($"[dim]{BandLabels(reading.TriggerScore)}[/]"),
			new Text(""),
			new Markup("[bold grey]KEY LEVELS & TELEMETRY[/]"),
			KeyLevels(reading, expand));
	}

	/// <summary>Why it scored that way: the factor breakdown and the setup checklist.</summary>
	private static Rows FactorsAndNotes(CascadeSetupEvaluation lead, int barWidth)
	{
		var rows = new List<Spectre.Console.Rendering.IRenderable> { FactorTable(lead.Factors, barWidth), new Text(""), new Markup("[bold grey]SETUP ANALYSIS[/]") };
		foreach (var note in lead.Notes)
			rows.Add(new Markup($"{NoteGlyph(note.Severity)} {Markup.Escape(note.Text)}"));
		return new Rows(rows);
	}

	/// <summary>The number of lines <paramref name="renderable"/> takes at <paramref name="width"/> on the current console.</summary>
	private static int HeightOf(Spectre.Console.Rendering.IRenderable renderable, int width) =>
		Spectre.Console.Rendering.Segment.SplitLines(renderable.Render(Spectre.Console.Rendering.RenderOptions.Create(AnsiConsole.Console), width), width).Count;

	private static Table RegimeComparisonTable(CascadeReading reading)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey23).Expand();
		foreach (var regime in new[] { CascadeRegime.BearCascade, CascadeRegime.BearTrap, CascadeRegime.BullCascade, CascadeRegime.BullTrap })
		{
			var isPrimary = reading.PrimaryRegime == regime;
			var isLeading = reading.LeadingSetup.Regime == regime;
			var color = RegimeColor(isPrimary ? regime : CascadeRegime.Neutral, regime);
			var badge = isPrimary ? $"[bold {color}]★ {CascadeReading.RegimeLabel(regime)}[/]"
				: isLeading ? $"[bold]{CascadeReading.RegimeLabel(regime)}[/]"
				: $"[grey]{CascadeReading.RegimeLabel(regime)}[/]";
			table.AddColumn(new TableColumn(badge).Centered().NoWrap());
		}

		var cells = new List<Markup>();
		foreach (var regime in new[] { CascadeRegime.BearCascade, CascadeRegime.BearTrap, CascadeRegime.BullCascade, CascadeRegime.BullTrap })
		{
			var eval = reading.Setups.TryGetValue(regime, out var e) ? e : new CascadeSetupEvaluation(regime, 0, false, Array.Empty<FactorScore>(), Array.Empty<SetupNote>());
			var confirmed = eval.Complete && eval.Score >= reading.TriggerScore;
			var color = confirmed ? RegimeColor(regime, regime) : (eval.Score >= 50 ? "yellow" : "grey");
			var status = confirmed ? "[green]✔ confirmed[/]" : (eval.Complete ? "[yellow]gates ✔[/]" : "[dim]gates ✘[/]");
			cells.Add(new Markup($"[bold {color}]{eval.Score}/100[/] · {status}"));
		}
		table.AddRow(cells.ToArray());
		return table;
	}

	private static Table FactorTable(IReadOnlyList<FactorScore> factors, int barWidth)
	{
		// One space between columns and none at the edges: the table sits in a half-width subpanel.
		var table = new Table().NoBorder().HideHeaders()
			.AddColumn(new TableColumn("").NoWrap().PadLeft(0).PadRight(1))
			.AddColumn(new TableColumn("").NoWrap().PadLeft(0).PadRight(1))
			.AddColumn(new TableColumn("").RightAligned().NoWrap().PadLeft(0).PadRight(1))
			.AddColumn(new TableColumn("").PadLeft(0).PadRight(0));
		foreach (var f in factors)
		{
			var bar = f.Points.HasValue ? FactorBar(f.Points.Value, f.Max, barWidth) : $"[grey23]{new string('━', barWidth)}[/]";
			var score = f.Points.HasValue ? $"[bold]{f.Points.Value,2}[/]/{f.Max}" : $"[dim]n/a/{f.Max}[/]";
			table.AddRow(
				new Markup(Markup.Escape(f.Name)),
				new Markup(bar),
				new Markup(score),
				new Markup($"[dim]{Markup.Escape(f.Detail)}[/]"));
		}
		return table;
	}

	private static Grid KeyLevels(CascadeReading reading, bool expand)
	{
		var t = reading.Terrain;
		var f = t.Front;
		var inp = reading.Inputs;
		var grid = new Grid { Expand = expand }
			.AddColumn(new GridColumn().NoWrap().PadRight(2))
			.AddColumn(new GridColumn().RightAligned().NoWrap());

		var otnTag = inp.SpotOpenToNowPct is { } otn
			? $" [{(otn >= 0m ? "green" : "red")}]({otn:+0.00;-0.00}% vs open)[/]"
			: "";
		grid.AddRow(new Markup("[grey]Current Price[/]"), new Markup($"[bold]${f.Spot:N2}[/]{otnTag}"));

		if (inp.Tape is { } tape)
		{
			var vwapSide = tape.Side == AI.Open.ZeroDte.VwapSide.Above ? "[green]above[/]" : tape.Side == AI.Open.ZeroDte.VwapSide.Below ? "[red]below[/]" : "[yellow]at[/]";
			grid.AddRow(
				new Markup("[grey]Session VWAP & Range[/]"),
				new Markup($"[bold]${tape.Vwap:N2}[/] ({vwapSide} {tape.MinutesHeldOnSide}m, {tape.VwapFlips} flips · L ${tape.SessionLow:N2} – H ${tape.SessionHigh:N2})"));
		}

		if (t.FrontSettled)
			grid.AddRow(new Markup("[grey]Front Expiry[/]"), new Markup($"[dim]{t.FrontExpiry:yyyy-MM-dd} settled at the close — flip, gravity and walls not shown[/]"));
		else
			grid.AddRow(
				new Markup("[grey]0DTE Gamma Flip[/]"),
				new Markup(f.Trigger.HasValue
					? $"{PctTag(f.Trigger.Value, f.Spot)} [bold {(f.ShortGamma ? "red" : "green")}]${f.Trigger.Value:N2}[/] [dim]({(f.ShortGamma ? "short γ" : "long γ")})[/]"
					: $"[dim]— ({(f.ShortGamma ? "net short γ" : "net long γ")})[/]"));

		grid.AddRow(
			new Markup($"[grey]Multi-Expiry GEX ({t.ExpiryCount} exp)[/]"),
			new Markup($"{(t.FlipMulti.HasValue ? $"flip [bold]${t.FlipMulti.Value:N2}[/] · " : "")}net [bold {(t.NetGexFractionMulti >= 0m ? "green" : "red")}]{t.NetGexFractionMulti:+0.0%;-0.0%}[/]"));

		if (!t.FrontSettled)
		{
			var pullText = t.NetPull0Dte.HasValue
				? $" · pull [{(t.NetPull0Dte.Value >= 0m ? "green" : "red")}]{t.NetPull0Dte.Value:+0.00;-0.00}σ[/]"
				: "";
			grid.AddRow(
				new Markup("[grey]0DTE Gravity Magnet[/]"),
				new Markup(t.Gravity0Dte.HasValue
					? $"{PctTag(t.Gravity0Dte.Value, f.Spot)} [bold]${t.Gravity0Dte.Value:N2}[/]{pullText}"
					: "[dim]—[/]"));

			var move = Math.Max(f.DailyMove ?? 0m, f.Spot * 0.005m);
			var callWallText = f.CallWall.HasValue
				? ((f.CallWall.Value - f.Spot) < 0.15m * move && t.OtmCallWall0Dte.HasValue && t.OtmCallWall0Dte.Value > f.CallWall.Value
					? $"[green]${f.CallWall.Value:N0}[/] [dim](ATM)[/] → [bold green]${t.OtmCallWall0Dte.Value:N0}[/]"
					: $"[green]${f.CallWall.Value:N0}[/]")
				: "[dim]—[/]";
			var putWallText = f.PutWall.HasValue
				? ((f.Spot - f.PutWall.Value) < 0.15m * move && t.OtmPutWall0Dte.HasValue && t.OtmPutWall0Dte.Value < f.PutWall.Value
					? $"[red]${f.PutWall.Value:N0}[/] [dim](ATM)[/] → [bold red]${t.OtmPutWall0Dte.Value:N0}[/]"
					: $"[red]${f.PutWall.Value:N0}[/]")
				: "[dim]—[/]";
			grid.AddRow(
				new Markup("[grey]0DTE Call / Put Walls[/]"),
				new Markup($"C {callWallText} · P {putWallText}"));
		}

		if (t.NetVexMulti != 0m)
		{
			grid.AddRow(
				new Markup($"[grey]Multi-Expiry Vanna ({t.ExpiryCount} exp)[/]"),
				new Markup($"[bold {(t.NetVexMulti >= 0m ? "green" : "red")}]{(t.NetVexMulti >= 0m ? "+" : "−")}{AnalyzeGexCommand.FormatCompactDollars(Math.Abs(t.NetVexMulti))}[/] [dim](call−put skew {t.NetVexFractionMulti:+0%;-0%})[/]"));
		}

		if (inp.VixNow.HasValue || inp.VixPriorClose.HasValue)
		{
			var vixNowStr = inp.VixNow.HasValue ? $"[bold]{inp.VixNow.Value:F2}[/]" : $"[dim]prior {inp.VixPriorClose:F2}[/]";
			var vOtn = inp.VixChangeFromOpenPct is { } vo ? $" [{(vo >= 0m ? "red" : "green")}]{vo:+0.0;-0.0}% vs open[/]" : "";
			var vCc = inp.VixChangeFromClosePct is { } vc ? $" [dim]({vc:+0.0;-0.0}% vs close)[/]" : "";
			grid.AddRow(new Markup("[grey]Intraday VIX[/]"), new Markup($"{vixNowStr}{vOtn}{vCc}"));
		}

		if (inp.SmileIndex.HasValue || inp.MaxPriorVix9dOverVix.HasValue)
		{
			var zTag = inp.SmileZScore20d is { } sz ? $", {sz:+0.0;-0.0}σ" : "";
			var smileStr = inp.SmileIndex.HasValue
				? $"SMILE [bold]{inp.SmileIndex.Value:F0}[/]{(inp.SmileChange1d.HasValue ? $" ({inp.SmileChange1d.Value:+0.0;-0.0}{zTag})" : (zTag.Length > 0 ? $" ({zTag.TrimStart(',', ' ')})" : ""))}"
				: "";
			var bwdStr = inp.MaxPriorVix9dOverVix.HasValue
				? $"3d VIX9D/VIX [bold {(inp.MaxPriorVix9dOverVix.Value > 0m ? "red" : "green")}]{inp.MaxPriorVix9dOverVix.Value * 100m:+0.0;-0.0}%[/]"
				: "";
			var sep = smileStr.Length > 0 && bwdStr.Length > 0 ? " · " : "";
			grid.AddRow(new Markup("[grey]Skew & Term Structure[/]"), new Markup($"{smileStr}{sep}{bwdStr}"));
		}

		if (f.DailyMove.HasValue && f.DailyMove.Value > 0m)
			grid.AddRow(new Markup("[grey]Daily Expected Move[/]"), new Markup($"[dim]±${f.DailyMove.Value:N2} ({f.DailyMove.Value / f.Spot:P2})[/]"));

		return grid;
	}

	private static string PctTag(decimal level, decimal spot)
	{
		var pct = (level - spot) / spot;
		return $"[{(pct >= 0m ? "green" : "red")}]({pct:+0.00%;-0.00%})[/]";
	}

	private static string ScoreBar(int score, int triggerScore, string activeColor)
	{
		var filled = (int)Math.Round(ScoreBarWidth * Math.Clamp(score, 0, 100) / 100m, MidpointRounding.AwayFromZero);
		var ticks = new[] { 30, 50, triggerScore, CascadeScreener.StrongScore }.Select(b => b * ScoreBarWidth / 100).ToHashSet();
		var sb = new System.Text.StringBuilder();
		for (var i = 0; i < ScoreBarWidth; i++)
		{
			var on = i < filled;
			var glyph = ticks.Contains(i) ? '┃' : '━';
			sb.Append(on ? $"[{activeColor}]{glyph}[/]" : $"[grey23]{glyph}[/]");
		}
		return sb.ToString();
	}

	private static string BandLabels(int triggerScore)
	{
		var line = new char[ScoreBarWidth];
		Array.Fill(line, ' ');
		Place(line, 0, "Quiet");
		Place(line, 30 * ScoreBarWidth / 100, "Watch");
		Place(line, 50 * ScoreBarWidth / 100, "Building");
		Place(line, triggerScore * ScoreBarWidth / 100, $"Trigger({triggerScore})");
		Place(line, ScoreBarWidth - "Strong".Length, "Strong");
		return new string(line);
	}

	private static void Place(char[] line, int at, string label)
	{
		if (at < 0 || at + label.Length > line.Length) return;
		label.CopyTo(0, line, at, label.Length);
	}

	private static string FactorBar(int points, int max, int width)
	{
		var filled = max > 0 ? (int)Math.Round(width * (decimal)points / max, MidpointRounding.AwayFromZero) : 0;
		var share = max > 0 ? (decimal)points / max : 0m;
		var color = share >= 0.70m ? "green" : share >= 0.35m ? "yellow" : "red";
		return $"[{color}]{new string('━', filled)}[/][grey23]{new string('━', width - filled)}[/]";
	}

	private static string NoteGlyph(NoteSeverity s) => s switch
	{
		NoteSeverity.Confirm => "[green]✔[/]",
		NoteSeverity.Warn => "[yellow]⚠[/]",
		NoteSeverity.Conflict => "[red]✘[/]",
		_ => "[dim]·[/]",
	};

	private static string RegimeColor(CascadeRegime primary, CascadeRegime fallback)
	{
		var r = primary != CascadeRegime.Neutral ? primary : fallback;
		return r switch
		{
			CascadeRegime.BearCascade => "red",
			CascadeRegime.BearTrap => "aqua",
			CascadeRegime.BullCascade => "green",
			CascadeRegime.BullTrap => "yellow",
			_ => "grey",
		};
	}
}
