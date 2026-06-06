using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;

namespace WebullAnalytics.AI;

internal sealed class AIConfigInitSettings : CommandSettings
{
	[CommandOption("--out <PATH>")]
	[Description("Write the generated base config to this path (relative to the data dir or absolute) instead of stdout.")]
	public string? Out { get; set; }
}

/// <summary>`wa ai config init [--out PATH]` — emits a COMPLETE base config: every parameter the schema
/// defines, at its code default, with all management rules and all opener structures forced to
/// <c>enabled: false</c>. Serialized from <see cref="AIConfig"/> itself, so it never drifts from the code.
/// Intended as the base layer (ai-config.json); per-ticker and per-(ticker,strategy) layers then override.</summary>
internal sealed class AIConfigInitCommand : AsyncCommand<AIConfigInitSettings>
{
	public override Task<int> ExecuteAsync(CommandContext context, AIConfigInitSettings settings, CancellationToken cancellationToken)
	{
		var cfg = new AIConfig();
		DisableEnabledFlags(cfg.Rules);                 // all management rules off in the base
		DisableEnabledFlags(cfg.Opener.Structures);     // all opener structures off in the base

		var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });

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
		return Task.FromResult(0);
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
