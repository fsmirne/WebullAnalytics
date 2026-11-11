using Spectre.Console;

namespace WebullAnalytics.Analyze;

/// <summary>
/// Terminal rendering of a <see cref="CascadeReading"/>, styled after <see cref="GexSqueezePanel"/>:
/// header with classified regime badge, 0–100 score bar,
/// 4-regime comparison row, 5-factor breakdown bars, key levels &amp; telemetry, and the setup checklist.
/// </summary>
internal static class CascadePanel
{
	private const int ScoreBarWidth = 60;
	private const int FactorBarWidth = 28;

	public static void Render(string ticker, DateTime asOf, string scope, CascadeReading reading)
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
			headerGrid.AddRow(
				new Markup($"[yellow]The session has closed: this is a recap of the whole day (full-session flow and tape), and nothing confirms after the close.{(reading.Terrain.FrontSettled ? " The front expiry has settled, so its gamma levels are not scored." : "")}[/]"),
				new Text(""));
		headerGrid.AddEmptyRow();

		var earned = lead.Factors.Sum(f => f.Points ?? 0);
		var possible = lead.Factors.Where(f => f.Points.HasValue).Sum(f => f.Max);
		var scoredCount = lead.Factors.Count(f => f.Points.HasValue);
		var coverage = $"{earned}/{possible} pts · {scoredCount} of {lead.Factors.Count} factors · trigger ≥{reading.TriggerScore}";
		headerGrid.AddRow(
			new Markup("[bold grey]SETUP SCORE[/]"),
			new Markup($"[bold {regimeColor}]{lead.Score}[/][bold]/100[/]"));
		headerGrid.AddRow(
			new Text(""),
			new Markup(closed ? $"[dim]{coverage} · recap[/]" : lead.Complete ? $"[dim]{coverage}[/]" : $"[yellow]{coverage} · awaiting confirmation gates[/]"));

		var rows = new List<Spectre.Console.Rendering.IRenderable>
		{
			headerGrid,
			new Markup(ScoreBar(lead.Score, reading.TriggerScore, regimeColor)),
			new Markup($"[dim]{BandLabels(reading.TriggerScore)}[/]"),
			new Text(""),
			new Markup("[bold grey]REGIME COMPARISON[/]"),
			RegimeComparisonTable(reading),
			new Text(""),
			new Markup($"[bold grey]FACTOR BREAKDOWN ({Markup.Escape(CascadeReading.RegimeLabel(lead.Regime))})[/]"),
			FactorTable(lead.Factors),
			new Text(""),
			new Panel(KeyLevels(reading)).Header("[bold grey]KEY LEVELS & TELEMETRY[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey).Expand(),
			new Text(""),
			new Markup("[bold grey]SETUP ANALYSIS[/]"),
		};

		foreach (var note in lead.Notes)
			rows.Add(new Markup($"{NoteGlyph(note.Severity)} {Markup.Escape(note.Text)}"));

		AnsiConsole.Write(new Panel(new Rows(rows)).Border(BoxBorder.Rounded).BorderColor(Color.Grey).Padding(2, 1));
		AnsiConsole.MarkupLine("[dim]Regimes: [red]BEAR CASCADE[/] = short-γ + put flow + intraday VIX expansion + VWAP breakdown. [aqua]BEAR TRAP[/] = gap-down / morning dip + intraday VIX stall + VWAP reclaim. [green]BULL CASCADE[/] = intraday VIX crush + call flow + overhead target wall + VWAP breakout. [yellow]BULL TRAP[/] = gap-up / morning rally + VIX failing to crush + VWAP roll-over. Describes the session so far, not what comes next: over 2022–2026 a confirmed regime at 09:50 ET showed no edge from there to the close.[/]");
	}

	private static Table RegimeComparisonTable(CascadeReading reading)
	{
		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey23).Expand();
		foreach (var regime in new[] { CascadeRegime.BearCascade, CascadeRegime.BearTrap, CascadeRegime.BullCascade, CascadeRegime.BullTrap })
		{
			var eval = reading.Setups[regime];
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
			var eval = reading.Setups[regime];
			var confirmed = eval.Complete && eval.Score >= reading.TriggerScore;
			var color = confirmed ? RegimeColor(regime, regime) : (eval.Score >= 50 ? "yellow" : "grey");
			var status = confirmed ? "[green]✔ confirmed[/]" : (eval.Complete ? "[yellow]gates ✔[/]" : "[dim]gates ✘[/]");
			cells.Add(new Markup($"[bold {color}]{eval.Score}/100[/] · {status}"));
		}
		table.AddRow(cells.ToArray());
		return table;
	}

	private static Table FactorTable(IReadOnlyList<FactorScore> factors)
	{
		var table = new Table().NoBorder().HideHeaders()
			.AddColumn(new TableColumn("").NoWrap())
			.AddColumn(new TableColumn("").NoWrap())
			.AddColumn(new TableColumn("").RightAligned().NoWrap())
			.AddColumn(new TableColumn(""));
		foreach (var f in factors)
		{
			var bar = f.Points.HasValue ? FactorBar(f.Points.Value, f.Max) : $"[grey23]{new string('━', FactorBarWidth)}[/]";
			var score = f.Points.HasValue ? $"[bold]{f.Points.Value,2}[/]/{f.Max}" : $"[dim]n/a/{f.Max}[/]";
			table.AddRow(
				new Markup(Markup.Escape(f.Name)),
				new Markup(bar),
				new Markup(score),
				new Markup($"[dim]{Markup.Escape(f.Detail)}[/]"));
		}
		return table;
	}

	private static Grid KeyLevels(CascadeReading reading)
	{
		var t = reading.Terrain;
		var f = t.Front;
		var inp = reading.Inputs;
		var grid = new Grid().Expand()
			.AddColumn(new GridColumn().NoWrap())
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

	private static string FactorBar(int points, int max)
	{
		var filled = max > 0 ? (int)Math.Round(FactorBarWidth * (decimal)points / max, MidpointRounding.AwayFromZero) : 0;
		var share = max > 0 ? (decimal)points / max : 0m;
		var color = share >= 0.70m ? "green" : share >= 0.35m ? "yellow" : "red";
		return $"[{color}]{new string('━', filled)}[/][grey23]{new string('━', FactorBarWidth - filled)}[/]";
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
