using Spectre.Console;
using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// The cascade panel's two subpanels sit side by side whenever their fixed columns fit; factor details and setup notes are prose and wrap,
// so a long one never stacks the panel (2026-11-16 09:38: an 84-column VIX detail and a 113-column VWAP note stacked it at 202 columns).
public class CascadePanelLayoutTests
{
	private const decimal Spot = 7512.53m;
	private static readonly DateTime Today = new(2026, 11, 16);

	private static CascadeReading Reading()
	{
		var terrain = new CascadeTerrain(new SqueezeTerrain(Spot, Trigger: Spot + 44m, ShortGamma: true, CallWall: 7610m, PutWall: 7450m, DailyMove: 86.90m), Today, null, null, null, null, 0m, 0m, 0m, 22);
		var inputs = new CascadeInputs(null, null, null, null, null, 20m, 20m, 20.59m, null, null, null, null);
		var factors = new[]
		{
			new FactorScore("Short-Gamma Terrain", 17, 20, "front short γ, 22exp net GEX -32%"),
			new FactorScore("Intraday VIX Expansion", 9, 20, "VIX 20.59, +3.0% vs open, +6.7% vs close, 3d VIX9D/VIX -6.4%, SMILE 2571 (-1, +0.6σ)"),
			new FactorScore("Put Flow & Pace", 3, 20, "58% put Δ-vol (-0.16), ΔOI -0.15"),
			new FactorScore("Gravity & Target Wall", 13, 20, "front gravity $7,500, target P-wall $7,450 (-63 pts)"),
			new FactorScore("VWAP Breakdown Tape", 0, 20, "spot -0.02% vs open (-0.02 EM), above VWAP (2m, 3 flips)"),
		};
		var notes = new[]
		{
			new SetupNote(NoteSeverity.Confirm, "Intraday VIX expanding (+3.0% vs open, +6.7% vs close) — confirms the move against a Bear Trap"),
			new SetupNote(NoteSeverity.Conflict, "Price is not holding ≥0.20 EM below the open on the lower side of VWAP for 5m ($7,499.09, above VWAP, -0.02 EM)"),
		};
		var lead = new CascadeSetupEvaluation(CascadeRegime.BearCascade, 42, false, factors, notes);
		return new CascadeReading(CascadeRegime.Neutral, lead, new Dictionary<CascadeRegime, CascadeSetupEvaluation> { [CascadeRegime.BearCascade] = lead }, terrain, inputs, CascadeScreener.DefaultTriggerScore);
	}

	private static string[] Render(int width)
	{
		var output = new StringWriter();
		var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
		console.Profile.Width = width;
		console.Write(CascadePanel.Build(console, "SPXW", Today.AddHours(9).AddMinutes(38), "22 expiration(s) ≤30DTE", Reading(), trade: null));
		return output.ToString().Split('\n');
	}

	[Fact]
	public void LongDetailsAndNotesWrapInsteadOfStackingThePanel()
	{
		var lines = Render(202);
		Assert.Contains(lines, l => l.Contains("SETUP SCORE", StringComparison.Ordinal) && l.Contains("FACTOR BREAKDOWN", StringComparison.Ordinal));
		Assert.All(lines, l => Assert.True(l.TrimEnd('\r').Length <= 202, l));
		// The wrapped detail and note are still all there.
		var text = string.Join(" ", lines.Select(l => l.Replace("│", " ")));
		Assert.Contains("SMILE", text);
		Assert.Contains("-0.02 EM)", text);
	}

	[Fact]
	public void ANarrowTerminalStacksTheSubpanels()
	{
		var lines = Render(140);
		Assert.DoesNotContain(lines, l => l.Contains("SETUP SCORE", StringComparison.Ordinal) && l.Contains("FACTOR BREAKDOWN", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.Contains("FACTOR BREAKDOWN", StringComparison.Ordinal));
	}
}
