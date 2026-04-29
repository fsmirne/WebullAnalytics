using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;

namespace WebullAnalytics.AI.RiskDiagnostics;

/// <summary>Writes a RiskDiagnostic to the Spectre console as a framed block. Pure rendering — all data
/// comes from the record. Used by both the manage and open pipelines.</summary>
internal static class RiskDiagnosticRenderer
{
	internal static void WriteConsole(IAnsiConsole console, RiskDiagnostic d) => console.Write(Build(d));
	internal static void WriteAscii(IAnsiConsole console, RiskDiagnostic d) => console.Write(Build(d, ascii: true));

	internal static IRenderable Build(RiskDiagnostic d, bool ascii = false)
	{
      var items = new List<(string Label, string Value)>
		{
			("Structure:", $"{Markup.Escape(d.StructureLabel)} ([italic]{Markup.Escape(d.DirectionalBias)}[/])"),
			("Greeks:", $"Δ {FormatDelta(d.NetDelta)}   θ {FormatDollars(d.NetThetaPerDay)}/day   ν {FormatDollars(d.NetVega)}/IV pt"),
			("DTE:", $"short {d.ShortLegDteMin}d  long {d.LongLegDteMax}d  gap {d.DteGapDays}d"),
			("Premium:", FormatPremium(d)),
			("Spot:", $"${d.SpotAtEvaluation.ToString("F2", CultureInfo.InvariantCulture)}  short OTM: {(d.ShortLegOtm ? "yes" : "no")}  short extrinsic ${d.ShortLegExtrinsic.ToString("F2", CultureInfo.InvariantCulture)}"),
		};

		if (d.Trend is TrendSnapshot t)
		{
			var intraday = t.ChangePctIntraday is decimal i
				? $"intraday {i.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  "
				: "";
			items.Add(("Trend:", $"5d {t.ChangePct5Day.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  20d {t.ChangePct20Day.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}%  {intraday}ATR14 {t.Atr14Pct.ToString("F1", CultureInfo.InvariantCulture)}%"));
		}

		if (d.UnrealizedPnlPerShare is decimal pnl)
		{
			var color = pnl >= 0m ? "green" : "red";
			items.Add(("P&L:", $"cost ${d.CostBasisPerShare!.Value.ToString("F2", CultureInfo.InvariantCulture)}  now ${d.CurrentValuePerShare!.Value.ToString("F2", CultureInfo.InvariantCulture)}  [{color}]{pnl.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}[/]/share"));
		}

		if (d.Probe is RiskDiagnosticProbe p)
		{
			if (p.EnumDelta.HasValue && p.EnumDeltaMin.HasValue && p.EnumDeltaMax.HasValue && p.EnumDeltaPass.HasValue)
			{
				var pass = p.EnumDeltaPass.Value ? "PASS" : "FAIL";
                items.Add(("Enum delta:", $"≈{p.EnumDelta.Value:F3} (band {p.EnumDeltaMin.Value:F2}-{p.EnumDeltaMax.Value:F2}) ⇒ {pass}"));
			}

			foreach (var q in p.LegQuotes)
			{
				var label = $"{CapProbeLabel(q.Label)} quote:";
				var bid = q.Bid.HasValue ? q.Bid.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
				var ask = q.Ask.HasValue ? q.Ask.Value.ToString("F2", CultureInfo.InvariantCulture) : "null";
				var mid = q.Bid.HasValue && q.Ask.HasValue
					? ((q.Bid.Value + q.Ask.Value) / 2m).ToString("F2", CultureInfo.InvariantCulture)
					: "null";
				var iv = q.ImpliedVolatility.HasValue ? q.ImpliedVolatility.Value.ToString("F3", CultureInfo.InvariantCulture) : "null";
				var hv = q.HistoricalVolatility.HasValue ? q.HistoricalVolatility.Value.ToString("F3", CultureInfo.InvariantCulture) : "null";
				var iv5 = q.ImpliedVolatility5Day.HasValue ? q.ImpliedVolatility5Day.Value.ToString("F3", CultureInfo.InvariantCulture) : "null";
				var oi = q.OpenInterest.HasValue ? q.OpenInterest.Value.ToString(CultureInfo.InvariantCulture) : "null";
				var vol = q.Volume.HasValue ? q.Volume.Value.ToString(CultureInfo.InvariantCulture) : "null";
				items.Add((label, $"bid={bid} ask={ask} mid={mid} iv={iv} hv={hv} iv5={iv5} oi={oi} vol={vol} sym={Markup.Escape(q.Symbol)}"));
			}

			if (p.OpenerScore is RiskDiagnosticOpenerScore s)
			{
				var margin = TryFormatMarginRequirement(s);
				if (margin != null)
					items.Add(("Margin:", margin));

				if (!string.IsNullOrWhiteSpace(s.Rationale))
				{
					var sections = s.Rationale
						.Replace("\r\n", "\n", StringComparison.Ordinal)
						.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

					if (sections.Length > 0)
						items.Add(("Rationale:", Markup.Escape(sections[0])));

                    if (sections.Length > 1)
						items.Add(("Score:", Markup.Escape(sections[1])));

                    if (sections.Length > 4)
					{
						items.Add(("Indicators:", Markup.Escape(sections[2])));
						items.Add(("Factors:", Markup.Escape(sections[3])));
						items.Add(("Result:", Markup.Escape(sections[4])));
					}
					else
					{
						if (sections.Length > 2)
							items.Add(("Factors:", Markup.Escape(sections[2])));

						if (sections.Length > 3)
							items.Add(("Result:", Markup.Escape(sections[3])));
					}
				}
			}
		}

		var labelWidth = items.Max(i => i.Label.Length);
		var lines = items.Select(i => $"[bold]{Markup.Escape(i.Label.PadRight(labelWidth))}[/] {i.Value}").ToList();

		if (d.Rules.Count > 0)
		{
			lines.Add("[bold]Rules fired:[/]");
			foreach (var r in d.Rules)
                lines.Add($"  {(ascii ? "-" : "•")} [cyan]{Markup.Escape(r.Id)}[/] {(ascii ? "-" : "—")} {Markup.Escape(r.Message)}");
		}

		return new Panel(string.Join("\n", lines))
			.Header("[white]Risk diagnostic[/]")
			.Expand()
			.Border(ascii ? BoxBorder.Ascii : BoxBorder.Rounded)
			.BorderColor(Color.Grey);
	}

	private static string FormatDelta(decimal d)
	{
		if (d == 0m)
			return "+0.00";

		var format = Math.Abs(d) < 0.01m ? "+0.0000;-0.0000" : "+0.00;-0.00";
		return d.ToString(format, CultureInfo.InvariantCulture);
	}
	private static string FormatDollars(decimal d) => (d >= 0m ? "+$" : "-$") + Math.Abs(d).ToString("F2", CultureInfo.InvariantCulture);
	private static string FormatRatio(decimal? r) => r is decimal v ? $" ({v.ToString("F1", CultureInfo.InvariantCulture)}× ratio)" : "";
	private static string FormatRatioDetailed(decimal? r) => r is decimal v ? $" ({v.ToString("F2", CultureInfo.InvariantCulture)}× ratio)" : "";
	private static string FormatNet(decimal n) => n >= 0m ? $"credit ${n.ToString("F2", CultureInfo.InvariantCulture)}" : $"debit ${Math.Abs(n).ToString("F2", CultureInfo.InvariantCulture)}";
	private static string FormatDebitCredit(decimal n) => n >= 0m ? $"debit ${n.ToString("F2", CultureInfo.InvariantCulture)}" : $"credit ${Math.Abs(n).ToString("F2", CultureInfo.InvariantCulture)}";

	private static string FormatPremium(RiskDiagnostic d)
	{
		if (d.MarketLongPremiumPaid.HasValue && d.MarketShortPremiumReceived.HasValue && d.MarketNetPremiumPerShare.HasValue && d.TheoreticalLongPremiumPaid.HasValue && d.TheoreticalShortPremiumReceived.HasValue && d.TheoreticalNetPremiumPerShare.HasValue)
			return $"market → long ${d.MarketLongPremiumPaid.Value.ToString("F2", CultureInfo.InvariantCulture)} / short ${d.MarketShortPremiumReceived.Value.ToString("F2", CultureInfo.InvariantCulture)}{FormatRatioDetailed(d.MarketPremiumRatio)}, net {FormatDebitCredit(d.MarketNetPremiumPerShare.Value)} | theoretical → long ${d.TheoreticalLongPremiumPaid.Value.ToString("F2", CultureInfo.InvariantCulture)} / short ${d.TheoreticalShortPremiumReceived.Value.ToString("F2", CultureInfo.InvariantCulture)}{FormatRatioDetailed(d.TheoreticalPremiumRatio)}, net {FormatDebitCredit(d.TheoreticalNetPremiumPerShare.Value)}";

		if (d.NetMidPerShare.HasValue && d.TheoreticalValuePerShare.HasValue)
			return $"MID: {FormatDebitCredit(d.NetMidPerShare.Value)} / Theoretical: {FormatDebitCredit(d.TheoreticalValuePerShare.Value)}";

		return $"long ${d.LongPremiumPaid.ToString("F2", CultureInfo.InvariantCulture)} / short ${d.ShortPremiumReceived.ToString("F2", CultureInfo.InvariantCulture)}{FormatRatio(d.PremiumRatio)}, net {FormatNet(d.NetCashPerShare)}";
	}

	private static string? TryFormatMarginRequirement(RiskDiagnosticOpenerScore s)
	{
		if (string.IsNullOrWhiteSpace(s.Structure) || s.Structure.Equals("probe", StringComparison.OrdinalIgnoreCase))
			return null;

		var perContract = RequiresMargin(s.Structure) ? s.CapitalAtRiskPerContract ?? 0m : 0m;
		var qty = Math.Max(1, s.Qty);
		var total = perContract * qty;
		if (qty <= 1)
			return perContract == 0m ? "$0" : $"${perContract.ToString("N2", CultureInfo.InvariantCulture)}/contract";

		return total == 0m ? "$0 total ($0/contract)" : $"${total.ToString("N2", CultureInfo.InvariantCulture)} total (${perContract.ToString("N2", CultureInfo.InvariantCulture)}/contract)";
	}

	private static bool RequiresMargin(string structure) =>
		structure.Equals(nameof(OpenStructureKind.ShortPutVertical), StringComparison.OrdinalIgnoreCase)
		|| structure.Equals(nameof(OpenStructureKind.ShortCallVertical), StringComparison.OrdinalIgnoreCase)
		|| structure.Equals(nameof(OpenStructureKind.IronButterfly), StringComparison.OrdinalIgnoreCase)
		|| structure.Equals(nameof(OpenStructureKind.IronCondor), StringComparison.OrdinalIgnoreCase);

	private static string CapProbeLabel(string label)
	{
		if (string.IsNullOrWhiteSpace(label)) return label;
		if (label.StartsWith("short", StringComparison.OrdinalIgnoreCase))
			return "Short" + label[5..];
		if (label.StartsWith("long", StringComparison.OrdinalIgnoreCase))
			return "Long" + label[4..];
		return char.ToUpperInvariant(label[0]) + label[1..];
	}
}
