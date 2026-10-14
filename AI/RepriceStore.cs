using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

/// <summary>
/// Persisted state of the orders <see cref="OrderRepricer"/> is walking, so a watch-loop restart resumes them instead of
/// leaving them at whatever price they had reached. Append-only JSONL: every change appends the order's full state and
/// the last line per client order id wins. A finished order gets a final line with <c>done</c> set and is ignored from
/// then on; at a handful of lines per order the file never needs pruning.
///
/// Shared by every process on the machine (several watch loops, `wa ai scan --submit`), so any watch loop on the same
/// account picks up an order another process placed. <see cref="TryLock"/> serializes the read-step-append cycle across
/// processes so two loops never step the same order in the same tick.
/// </summary>
internal sealed class RepriceStore
{
	internal sealed record State(
		[property: JsonPropertyName("account")] string AccountId,
		[property: JsonPropertyName("clientOrderId")] string ClientOrderId,
		[property: JsonPropertyName("ticker")] string Ticker,
		[property: JsonPropertyName("side")] string Side,
		[property: JsonPropertyName("legs")] int LegCount,
		[property: JsonPropertyName("open")] bool IsOpen,
		[property: JsonPropertyName("originalLimit")] decimal OriginalLimit,
		[property: JsonPropertyName("limit")] decimal CurrentLimit,
		[property: JsonPropertyName("steps")] int Steps,
		// Null until the first tick that sees the order — every timestamp comes from the watch loop's clock.
		[property: JsonPropertyName("lastStepAt")] DateTime? LastStepAt,
		[property: JsonPropertyName("done")] bool Done = false);

	private readonly string _path;
	private readonly string _lockPath;

	public RepriceStore(string path)
	{
		_path = path;
		_lockPath = path + ".lock";
	}

	internal static RepriceStore Default() => new(Program.ResolvePath("data/reprice-orders.jsonl"));

	public void Append(State state)
	{
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.AppendAllText(_path, JsonSerializer.Serialize(state) + "\n");
	}

	/// <summary>Latest state of every unfinished order on <paramref name="accountId"/>, in first-tracked order. Corrupt
	/// lines (a torn concurrent append) are skipped.</summary>
	public IReadOnlyList<State> Active(string accountId)
	{
		if (!File.Exists(_path)) return Array.Empty<State>();
		var latest = new Dictionary<string, State>();
		var order = new List<string>();
		foreach (var line in File.ReadAllLines(_path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			State? s = null;
			try { s = JsonSerializer.Deserialize<State>(line); } catch (JsonException) { }
			if (s == null || s.AccountId != accountId) continue;
			if (!latest.ContainsKey(s.ClientOrderId)) order.Add(s.ClientOrderId);
			latest[s.ClientOrderId] = s;
		}
		return order.Select(id => latest[id]).Where(s => !s.Done).ToList();
	}

	/// <summary>Exclusive cross-process lock for one repricing pass, or null when another process holds it (that process
	/// is stepping the same orders right now; this one skips the tick).</summary>
	public IDisposable? TryLock()
	{
		var dir = Path.GetDirectoryName(_lockPath);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		try { return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
		catch (IOException) { return null; }
	}
}
