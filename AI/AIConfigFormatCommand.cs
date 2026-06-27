using System.ComponentModel;
using System.Text.Json.Nodes;
using Spectre.Console.Cli;

namespace WebullAnalytics.AI;

internal sealed class AIConfigFormatSettings : CommandSettings
{
	[CommandArgument(0, "<PATH>")]
	[Description("JSON config file to rewrite in house style (tab indent, LF, one-line opener.structures entries).")]
	public string Path { get; set; } = "";
}

/// <summary>`wa ai config format &lt;PATH&gt;` — rewrites an existing JSON config in the house style
/// (<see cref="ConfigJsonWriter"/>): tab-indented, LF, with each opener.structures entry on one line.
/// Use it to normalize hand-edited or generated config files.</summary>
internal sealed class AIConfigFormatCommand : AsyncCommand<AIConfigFormatSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, AIConfigFormatSettings settings, CancellationToken cancellationToken)
	{
		var path = Program.ResolvePath(settings.Path);
		if (!File.Exists(path)) { Console.Error.WriteLine($"Error: file not found: {settings.Path}"); return Task.FromResult(1); }

		JsonNode? node;
		try { node = JsonNode.Parse(File.ReadAllText(path)); }
		catch (Exception ex) { Console.Error.WriteLine($"Error: failed to parse '{settings.Path}': {ex.Message}"); return Task.FromResult(1); }
		if (node == null) { Console.Error.WriteLine("Error: empty JSON."); return Task.FromResult(1); }

		File.WriteAllText(path, ConfigJsonWriter.Serialize(node));
		Console.WriteLine($"Reformatted {settings.Path}");
		return Task.FromResult(0);
	}
}
