using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebullAnalytics;

/// <summary>House JSON formatter for ai-config files: tab-indented, LF-terminated, but with each
/// <c>opener.structures.*</c> entry collapsed onto a single line (and arrays kept inline) so the
/// per-structure knob sets are easy to scan at a glance. Everything else is one key per line.</summary>
internal static class ConfigJsonWriter
{
	/// <summary>Object-valued keys whose contents are collapsed onto a single line (small knob bags that
	/// read better inline). <c>structures</c> is handled separately (container multi-line, each entry inline).</summary>
	private static readonly HashSet<string> InlineObjectKeys = new(StringComparer.Ordinal) { "events" };

	/// <summary>Canonical top-level key order (matches the AIConfig property order), so base/ticker/strategy
	/// files all read consistently regardless of how they were built. Unlisted keys keep their order, after.</summary>
	private static readonly string[] RootKeyOrder = { "defaultStrategy", "watch", "cashReserve", "log-level", "indicators", "rules", "opener", "autoExecute" };

	public static string Serialize(JsonNode root)
	{
		var sb = new StringBuilder();
		WriteValue(sb, root, 0, compact: false);
		sb.Append('\n');
		return sb.ToString();
	}

	private static void WriteValue(StringBuilder sb, JsonNode? node, int depth, bool compact)
	{
		switch (node)
		{
			case JsonObject obj: WriteObject(sb, obj, depth, compact); break;
			case JsonArray arr: WriteArray(sb, arr); break;   // arrays always inline
			default: sb.Append(node?.ToJsonString() ?? "null"); break;
		}
	}

	private static void WriteObject(StringBuilder sb, JsonObject obj, int depth, bool compact)
	{
		if (obj.Count == 0) { sb.Append("{}"); return; }

		if (compact)
		{
			sb.Append("{ ");
			var first = true;
			foreach (var kv in obj)
			{
				if (!first) sb.Append(", ");
				first = false;
				sb.Append(JsonSerializer.Serialize(kv.Key)).Append(": ");
				WriteValue(sb, kv.Value, depth, compact: true);
			}
			sb.Append(" }");
			return;
		}

		sb.Append("{\n");
		var items = obj.ToList();
		if (depth == 0)   // normalize top-level key order so all config layers read consistently
			items = items.OrderBy(kv => { var idx = Array.IndexOf(RootKeyOrder, kv.Key); return idx < 0 ? int.MaxValue : idx; }).ToList();
		for (var i = 0; i < items.Count; i++)
		{
			var kv = items[i];
			Indent(sb, depth + 1);
			sb.Append(JsonSerializer.Serialize(kv.Key)).Append(": ");
			// opener.structures: container stays one-entry-per-line, but each structure's knobs go inline.
			if (kv.Key == "structures" && kv.Value is JsonObject structures)
				WriteStructuresContainer(sb, structures, depth + 1);
			else if (InlineObjectKeys.Contains(kv.Key) && kv.Value is JsonObject)
				WriteValue(sb, kv.Value, depth + 1, compact: true);
			else
				WriteValue(sb, kv.Value, depth + 1, compact: false);
			if (i < items.Count - 1) sb.Append(',');
			sb.Append('\n');
		}
		Indent(sb, depth);
		sb.Append('}');
	}

	private static void WriteStructuresContainer(StringBuilder sb, JsonObject structures, int depth)
	{
		if (structures.Count == 0) { sb.Append("{}"); return; }
		sb.Append("{\n");
		var items = structures.ToList();
		for (var i = 0; i < items.Count; i++)
		{
			var kv = items[i];
			Indent(sb, depth + 1);
			sb.Append(JsonSerializer.Serialize(kv.Key)).Append(": ");
			WriteValue(sb, kv.Value, depth + 1, compact: true);   // each structure on one line
			if (i < items.Count - 1) sb.Append(',');
			sb.Append('\n');
		}
		Indent(sb, depth);
		sb.Append('}');
	}

	private static void WriteArray(StringBuilder sb, JsonArray arr)
	{
		if (arr.Count == 0) { sb.Append("[]"); return; }
		sb.Append('[');
		for (var i = 0; i < arr.Count; i++)
		{
			if (i > 0) sb.Append(", ");
			WriteValue(sb, arr[i], 0, compact: true);
		}
		sb.Append(']');
	}

	private static void Indent(StringBuilder sb, int depth) => sb.Append('\t', depth);
}
