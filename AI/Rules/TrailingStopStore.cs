using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// The highest mark each trailed position has reached (<see cref="TrailingStopRule"/>). With a path the peaks are a small JSON file, so a
/// restarted watch loop trails from the peak the last one saw instead of from the current mark; without one (backtest, replay) they live
/// in memory. Several processes may share the file: each raise re-reads it and keeps the higher peak per key.
/// </summary>
internal sealed class TrailingStopStore
{
	internal sealed record Peak([property: JsonPropertyName("high")] decimal High, [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);

	/// <summary>A peak not raised for this long belongs to a position that is gone.</summary>
	private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

	private readonly string? _path;
	private readonly Dictionary<string, Peak> _peaks = new(StringComparer.Ordinal);

	public TrailingStopStore(string? path = null)
	{
		_path = path;
		foreach (var (key, peak) in ReadFile()) _peaks[key] = peak;
	}

	internal static TrailingStopStore Default() => new(Program.ResolvePath("data/trailing-stops.json"));

	/// <summary>Records <paramref name="mark"/> if it tops the stored peak for <paramref name="key"/> and returns the peak.</summary>
	public decimal Raise(string key, decimal mark, DateTime now)
	{
		if (_peaks.TryGetValue(key, out var known) && known.High >= mark) return known.High;
		foreach (var (k, peak) in ReadFile())
			if (!_peaks.TryGetValue(k, out var mine) || peak.High > mine.High) _peaks[k] = peak;
		if (!_peaks.TryGetValue(key, out known) || known.High < mark) _peaks[key] = known = new Peak(mark, now);
		foreach (var stale in _peaks.Where(kv => now - kv.Value.UpdatedAt > Retention).Select(kv => kv.Key).ToList()) _peaks.Remove(stale);
		WriteFile();
		return known.High;
	}

	private Dictionary<string, Peak> ReadFile()
	{
		if (_path == null || !File.Exists(_path)) return new Dictionary<string, Peak>();
		try { return JsonSerializer.Deserialize<Dictionary<string, Peak>>(File.ReadAllText(_path)) ?? new Dictionary<string, Peak>(); }
		catch (Exception ex) when (ex is JsonException or IOException) { return new Dictionary<string, Peak>(); }
	}

	private void WriteFile()
	{
		if (_path == null) return;
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.WriteAllText(_path, JsonSerializer.Serialize(_peaks));
	}
}
