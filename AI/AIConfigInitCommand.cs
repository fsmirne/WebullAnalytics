using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;

namespace WebullAnalytics.AI;

internal sealed class AIConfigInitSettings : CommandSettings
{
	[CommandArgument(0, "[TICKER]")]
	[Description("Optional ticker. When given, scaffolds a per-ticker override (ai-config.<TICKER>.json) plus a starter strategy layer (ai-config.<TICKER>.<STRATEGY>.json) instead of the complete base config. strikeStep and ivDefaultPct are derived from the live chain.")]
	public string? Ticker { get; set; }

	[CommandOption("--strategy <TOKEN>")]
	[Description("Ticker mode: strategy-layer token for the scaffolded ai-config.<TICKER>.<STRATEGY>.json (default: IC). The per-ticker file's defaultStrategy is set to this, so `wa ai scan <TICKER>` runs with no --strategy.")]
	public string Strategy { get; set; } = "IC";

	[CommandOption("--vendor <VENDOR>")]
	[Description("Ticker mode: live chain vendor for strikeStep/IV derivation — webull or schwab. Defaults to config.json's vendor.")]
	public string? Vendor { get; set; }

	[CommandOption("--force")]
	[Description("Ticker mode: overwrite existing ai-config.<TICKER>*.json files (default: refuse and exit).")]
	public bool Force { get; set; }

	[CommandOption("--out <PATH>")]
	[Description("Base mode: write the generated base config to this file (relative to the data dir or absolute) instead of stdout. Ticker mode: directory to write the scaffolded files into (default: the data dir).")]
	public string? Out { get; set; }
}

/// <summary>`wa ai config init [TICKER] [--out PATH]`. Without a ticker: emits a COMPLETE base config —
/// every parameter the schema defines, at its code default, with all management rules and all opener
/// structures forced to <c>enabled: false</c>. Serialized from <see cref="AIConfig"/> itself, so it never
/// drifts from the code. Intended as the base layer (ai-config.json). With a ticker: scaffolds a minimal
/// per-ticker override plus a starter strategy layer (see <see cref="AIConfigInitTicker"/>) — the loader
/// requires a strategy layer to exist, so a lone per-ticker file would not be runnable.</summary>
internal sealed class AIConfigInitCommand : AsyncCommand<AIConfigInitSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, AIConfigInitSettings settings, CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(settings.Ticker))
			return AIConfigInitTicker.RunAsync(settings, cancellationToken);

		return Task.FromResult(RunBase(settings));
	}

	/// <summary>Base-config mode: the complete, all-disabled schema dump.</summary>
	private static int RunBase(AIConfigInitSettings settings)
	{
		var cfg = new AIConfig();
		DisableEnabledFlags(cfg.Rules);                 // all management rules off in the base
		DisableEnabledFlags(cfg.Opener.Structures);     // all opener structures off in the base

		var json = ConfigJsonWriter.Serialize(JsonSerializer.SerializeToNode(cfg, JsonDefaults.Indented)!);

		if (!string.IsNullOrWhiteSpace(settings.Out))
		{
			var path = Program.ResolvePath(settings.Out!);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, json);
			Console.WriteLine($"Wrote complete base config ({json.Length:N0} bytes) to {settings.Out}");
		}
		else
		{
			Console.WriteLine(json);
		}
		return 0;
	}

	/// <summary>Sets every immediate sub-object's bool <c>Enabled</c> property to false. Reflection-based so
	/// it stays correct as rules/structures are added — no hard-coded list to maintain.</summary>
	private static void DisableEnabledFlags(object container)
	{
		foreach (var prop in container.GetType().GetProperties())
		{
			var child = prop.GetValue(container);
			if (child is null || child is string) continue;
			var enabled = child.GetType().GetProperty("Enabled");
			if (enabled is { CanWrite: true } && enabled.PropertyType == typeof(bool))
				enabled.SetValue(child, false);
		}
	}
}
