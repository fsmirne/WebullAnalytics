using System.Text.Json;

namespace WebullAnalytics;

static class JsonElementExtensions
{
	internal static bool TryGetString(this Dictionary<string, JsonElement> cfg, string key, out string value)
	{
		value = "";
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return false;
		value = el.GetString()!;
		return true;
	}

	internal static bool TryGetBool(this Dictionary<string, JsonElement> cfg, string key, out bool value)
	{
		value = false;
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
		value = el.GetBoolean();
		return true;
	}

	internal static bool TryGetDecimal(this Dictionary<string, JsonElement> cfg, string key, out decimal value)
	{
		value = 0;
		if (!cfg.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Number) return false;
		value = el.GetDecimal();
		return true;
	}
}
