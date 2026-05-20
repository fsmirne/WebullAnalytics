using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebullAnalytics.AI;

/// <summary>
/// Layered ai-config loading: base <c>ai-config.json</c> plus optional ticker-scoped overrides
/// at <c>ai-config.&lt;TICKER&gt;.json</c>. The override file is merged on top of the base via a
/// deep merge — JSON objects merge key-by-key (recursively); arrays and scalar leaves are
/// replaced wholesale by the override. This matches user expectations for tuning knobs
/// (override the value, don't accumulate) while still letting the base provide structure.
///
/// Either file alone is a valid config — when only one exists, that file is the config. When
/// both exist, the override wins on every overlapping key. Returns null with a stderr message
/// if neither file exists or the JSON is unparseable.
/// </summary>
internal static class AIConfigMerge
{
	/// <summary>Loads <paramref name="basePath"/>, optionally merges <paramref name="overridePath"/>
	/// on top, and deserializes the result as <see cref="AIConfig"/>. Either path may be null or
	/// non-existent; at least one of them must point to a valid JSON file.</summary>
	public static AIConfig? LoadMerged(string? basePath, string? overridePath)
	{
		var baseNode = TryReadJsonNode(basePath);
		var overrideNode = TryReadJsonNode(overridePath);

		if (baseNode == null && overrideNode == null)
			return null;

		var merged = DeepMerge(baseNode, overrideNode);
		if (merged == null) return null;

		try
		{
			return JsonSerializer.Deserialize<AIConfig>(merged.ToJsonString());
		}
		catch (JsonException ex)
		{
			Console.Error.WriteLine($"Error: failed to deserialize merged ai-config: {ex.Message}");
			return null;
		}
	}

	private static JsonNode? TryReadJsonNode(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
		try
		{
			return JsonNode.Parse(File.ReadAllText(path));
		}
		catch (JsonException ex)
		{
			Console.Error.WriteLine($"Error: failed to parse '{path}': {ex.Message}");
			return null;
		}
	}

	/// <summary>Recursively merges <paramref name="overrideNode"/> onto <paramref name="baseNode"/>.
	/// Object-vs-object: union keys, recursing on overlaps. Anything else: override replaces base.</summary>
	internal static JsonNode? DeepMerge(JsonNode? baseNode, JsonNode? overrideNode)
	{
		if (overrideNode == null) return baseNode?.DeepClone();
		if (baseNode == null) return overrideNode.DeepClone();

		if (baseNode is JsonObject baseObj && overrideNode is JsonObject overrideObj)
		{
			var merged = new JsonObject();
			foreach (var kvp in baseObj)
				merged[kvp.Key] = kvp.Value?.DeepClone();
			foreach (var kvp in overrideObj)
			{
				if (merged.TryGetPropertyValue(kvp.Key, out var existing))
				{
					merged.Remove(kvp.Key);
					merged[kvp.Key] = DeepMerge(existing, kvp.Value);
				}
				else
				{
					merged[kvp.Key] = kvp.Value?.DeepClone();
				}
			}
			return merged;
		}

		// Arrays and scalars: the override replaces the base outright. (Merging arrays is rarely
		// what the user means for tuning knobs — e.g. "tickers", "structures.ironCondor.widthSteps".)
		return overrideNode.DeepClone();
	}
}
