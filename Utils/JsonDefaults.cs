using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics;

/// <summary>House JSON serializer options. On-disk config/data files and pretty-printed output use
/// <b>tab</b> indentation (matches the hand-maintained ai-config.* files). Centralized so every
/// indented serializer is consistent — change the indent style here, not at each call site.</summary>
internal static class JsonDefaults
{
	/// <summary>Pretty-printed, tab-indented.</summary>
	public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 };

	/// <summary>Pretty-printed, tab-indented, omitting null-valued properties (for diagnostic dumps).</summary>
	public static readonly JsonSerializerOptions IndentedSkipNulls = new()
	{
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
}
