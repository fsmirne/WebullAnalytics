using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

/// <summary>
/// Persisted same-day ledger of orders THIS machine placed via the auto-executors. Webull's
/// today-orders history endpoint is eventually-consistent — a filled order can take 10+ minutes to
/// appear (observed live 2026-06-11: a DoubleDiagonal's two combo fills were invisible to /history
/// while the positions endpoint already showed all four legs, letting a second DD through the daily
/// cap). During that window the broker-truth snapshot sees neither the order (instant fills leave
/// /open immediately) nor the fill, so the daily cap and leg-set dedup are blind to our own
/// submissions. The ledger bridges the gap: every successful PlaceOrder appends a line here, and
/// <see cref="BrokerStateService"/> merges today's entries into its snapshot until the broker starts
/// reporting the order's client_order_id — from then on broker truth (with its canceled/rejected
/// handling) governs that order again.
///
/// Append-only JSONL keyed by ET date; survives restarts and is shared across concurrent watch/scan
/// processes by construction (re-read on every broker refresh). Old days are simply skipped on read —
/// at one or two lines per trading day the file never needs pruning.
/// </summary>
internal sealed class LocalOrderLedger
{
	internal sealed record Entry(
		[property: JsonPropertyName("date")] string Date,
		[property: JsonPropertyName("root")] string Root,
		[property: JsonPropertyName("open")] bool Open,
		[property: JsonPropertyName("fingerprint")] string Fingerprint,
		[property: JsonPropertyName("clientOrderId")] string? ClientOrderId);

	private readonly string _path;

	public LocalOrderLedger(string path) { _path = path; }

	public void Append(string root, bool open, string fingerprint, string? clientOrderId, DateTime etNow)
	{
		var entry = new Entry(etNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), root.ToUpperInvariant(), open, fingerprint, clientOrderId);
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n");
	}

	/// <summary>Today's entries (ET date match). Corrupt lines are skipped — a torn concurrent append
	/// must not take down the refresh that's protecting live submission.</summary>
	public IReadOnlyList<Entry> ReadFor(DateTime etDate)
	{
		if (!File.Exists(_path)) return Array.Empty<Entry>();
		var date = etDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var entries = new List<Entry>();
		foreach (var line in File.ReadAllLines(_path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			Entry? e = null;
			try { e = JsonSerializer.Deserialize<Entry>(line); } catch (JsonException) { }
			if (e != null && e.Date == date) entries.Add(e);
		}
		return entries;
	}
}
