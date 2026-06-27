using System.Text.Json.Nodes;
using Spectre.Console.Cli;

namespace WebullAnalytics.AI;

internal sealed class AIConfigShowSettings : AISingleTickerSubcommandSettings { }

/// <summary>`wa ai config show <TICKER> [--strategy TOK]` — prints the fully-resolved effective config
/// for a (ticker, strategy) run, tagging every leaf with the layer that set it (base / ticker / strategy).
/// Uses the same <see cref="AIContext.ResolveLayers"/> the loader uses, so what you see is what runs.</summary>
internal sealed class AIConfigShowCommand : AsyncCommand<AIConfigShowSettings>
{
	protected override Task<int> ExecuteAsync(CommandContext context, AIConfigShowSettings settings, CancellationToken cancellationToken)
	{
		var layers = AIContext.ResolveLayers(settings, out var ticker, out var strategy);
		if (layers == null) return Task.FromResult(1);

		// Read each layer's raw JSON; fold a merged view to enumerate effective leaves.
		var layerNodes = new List<(string Label, JsonNode Node)>();
		JsonNode? merged = null;
		foreach (var layer in layers)
		{
			JsonNode? node;
			try { node = JsonNode.Parse(File.ReadAllText(layer.AbsPath)); }
			catch (Exception ex) { Console.Error.WriteLine($"Error: failed to parse '{layer.AbsPath}': {ex.Message}"); return Task.FromResult(1); }
			if (node == null) continue;
			layerNodes.Add((layer.Label, node));
			merged = AIConfigMerge.DeepMerge(merged, node);
		}
		if (merged == null) { Console.Error.WriteLine("Error: ai-config is empty."); return Task.FromResult(1); }

		var leaves = new SortedDictionary<string, string>(StringComparer.Ordinal);
		FlattenLeaves(merged, "", leaves);

		Console.WriteLine($"{ticker} / {strategy}  —  effective config ({layers.Count} layer(s), most-specific wins)");
		foreach (var layer in layers)
			Console.WriteLine($"  {layer.Label,-16} {layer.AbsPath}");
		Console.WriteLine();

		var pad = leaves.Count == 0 ? 0 : leaves.Keys.Max(k => k.Length);
		foreach (var (path, value) in leaves)
		{
			// Source = the most-specific (last) layer that defines this leaf path.
			var source = "?";
			foreach (var (label, node) in layerNodes)
				if (PathExists(node, path)) source = label;
			Console.WriteLine($"{path.PadRight(pad)} = {value,-10}  [{source}]");
		}
		return Task.FromResult(0);
	}

	private static void FlattenLeaves(JsonNode? node, string prefix, SortedDictionary<string, string> outMap)
	{
		if (node is JsonObject obj)
		{
			foreach (var kv in obj)
				FlattenLeaves(kv.Value, prefix.Length == 0 ? kv.Key : $"{prefix}.{kv.Key}", outMap);
		}
		else // scalar or array: treat as a single leaf (arrays merge wholesale, so they have one source)
		{
			outMap[prefix] = node?.ToJsonString() ?? "null";
		}
	}

	private static bool PathExists(JsonNode? node, string dottedPath)
	{
		JsonNode? cur = node;
		foreach (var part in dottedPath.Split('.'))
		{
			if (cur is JsonObject obj && obj.TryGetPropertyValue(part, out var next)) cur = next;
			else return false;
		}
		return true;
	}
}
