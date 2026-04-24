using System.Globalization;
using Spectre.Console;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Writes a RiskDiagnostic to the Spectre console as a framed block. Pure rendering — all data
/// comes from the record. Used by both the manage and open pipelines.</summary>
internal static class RiskDiagnosticRenderer
{
	internal static void WriteConsole(IAnsiConsole console, RiskDiagnostic d)
	{
		var lines = new List<string>
		{
			$"[bold]Structure:[/] {Markup.Escape(d.StructureLabel)} ([italic]{Markup.Escape(d.DirectionalBias)}[/])",
			$"[bold]Greeks:[/]    Δ {FormatDelta(d.NetDelta)}   θ {FormatDollars(d.NetThetaPerDay)}/day   ν {FormatDollars(d.NetVega)}/IV pt",
			$"[bold]DTE:[/]       short {d.ShortLegDteMin}d  long {d.LongLegDteMax}d  gap {d.DteGapDays}d",
			$"[bold]Premium:[/]   long ${d.LongPremiumPaid.ToString("F2", CultureInfo.InvariantCulture)} / short ${d.ShortPremiumReceived.ToString("F2", CultureInfo.InvariantCulture)}{FormatRatio(d.PremiumRatio)}, net {FormatNet(d.NetCashPerShare)}",
			$"[bold]Spot:[/]      ${d.SpotAtEvaluation.ToString("F2", CultureInfo.InvariantCulture)}  short OTM: {(d.ShortLegOtm ? "yes" : "no")}  short extrinsic ${d.ShortLegExtrinsic.ToString("F2", CultureInfo.InvariantCulture)}",
		};

		if (d.Trend is TrendSnapshot t)
		{
			var intraday = t.ChangePctIntraday is decimal i
				? $"intraday {i.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  "
				: "";
			lines.Add($"[bold]Trend:[/]     5d {t.ChangePct5Day.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  20d {t.ChangePct20Day.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  {intraday}ATR20 {t.Spot20DayAtrPct.ToString("F1", CultureInfo.InvariantCulture)}%");
		}

		if (d.UnrealizedPnlPerShare is decimal pnl)
		{
			var color = pnl >= 0m ? "green" : "red";
			lines.Add($"[bold]P&L:[/]       cost ${d.CostBasisPerShare!.Value.ToString("F2", CultureInfo.InvariantCulture)}  now ${d.CurrentValuePerShare!.Value.ToString("F2", CultureInfo.InvariantCulture)}  [{color}]{pnl.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}[/]/share");
		}

		if (d.Rules.Count > 0)
		{
			lines.Add("[bold]Rules fired:[/]");
			foreach (var r in d.Rules)
				lines.Add($"  • [cyan]{Markup.Escape(r.Id)}[/] — {Markup.Escape(r.Message)}");
		}

		var panel = new Panel(string.Join("\n", lines))
			.Header("[white]Risk diagnostic[/]")
			.BorderColor(Color.Grey);
		console.Write(panel);
	}

	private static string FormatDelta(decimal d) => d.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
	private static string FormatDollars(decimal d) => (d >= 0m ? "+$" : "-$") + Math.Abs(d).ToString("F2", CultureInfo.InvariantCulture);
	private static string FormatRatio(decimal? r) => r is decimal v ? $" ({v.ToString("F1", CultureInfo.InvariantCulture)}× ratio)" : "";
	private static string FormatNet(decimal n) => n >= 0m
		? $"credit ${n.ToString("F2", CultureInfo.InvariantCulture)}"
		: $"debit ${Math.Abs(n).ToString("F2", CultureInfo.InvariantCulture)}";
}
