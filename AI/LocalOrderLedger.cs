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
/// submissions. The ledger bridges the gap: every PlaceOrder is recorded here, and
/// <see cref="BrokerStateService"/> merges today's entries into its snapshot until the broker starts
/// reporting the order's client_order_id — from then on broker truth (with its canceled/rejected
/// handling) governs that order again.
///
/// An order is recorded BEFORE it is sent (<see cref="Sending"/>), then marked <see cref="Placed"/> on a broker acknowledgement or
/// <see cref="Void"/> when it definitely never became an order (see <see cref="OrderPlacement"/>). An order sent with no answer back — a
/// timeout, a dropped connection, a 5xx — stays <see cref="Sending"/>: Webull may have taken it, so it blocks a re-send exactly as a placed
/// order does until the broker reports its id.
///
/// Append-only JSONL keyed by ET date; survives restarts and is shared across concurrent watch/scan
/// processes by construction (re-read on every broker refresh). An order's later lines (placed / void)
/// supersede its earlier ones. Old days are simply skipped on read — at a few lines per trading day the
/// file never needs pruning.
/// </summary>
internal sealed class LocalOrderLedger
{
	/// <summary>Recorded before the send; still the state when the send's outcome never came back.</summary>
	internal const string Sending = "sending";
	/// <summary>The broker acknowledged the order. Lines written before states existed carry no state and read as this.</summary>
	internal const string Placed = "placed";
	/// <summary>The order definitely never reached the broker, or the broker refused it: free to retry.</summary>
	internal const string Void = "void";

	internal sealed record Entry(
		[property: JsonPropertyName("date")] string Date,
		[property: JsonPropertyName("root")] string Root,
		[property: JsonPropertyName("open")] bool Open,
		[property: JsonPropertyName("fingerprint")] string Fingerprint,
		[property: JsonPropertyName("clientOrderId")] string? ClientOrderId,
		[property: JsonPropertyName("state")] string? State = null,
		[property: JsonPropertyName("at")] DateTime? At = null)
	{
		/// <summary>Sent, but no broker acknowledgement on record.</summary>
		[JsonIgnore] internal bool Unconfirmed => State == Sending;
	}

	private readonly string _path;

	public LocalOrderLedger(string path) { _path = path; }

	/// <summary>The ledger every order-placing command shares (watch/scan executors, `wa trade place`, `wa trade close`), so a
	/// manual order counts toward the opener's daily cap the moment it is placed.</summary>
	internal static LocalOrderLedger Default() => new(Program.ResolvePath("data/local-orders.jsonl"));

	public void Append(string root, bool open, string fingerprint, string? clientOrderId, DateTime etNow, string state = Placed)
	{
		var entry = new Entry(etNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), root.ToUpperInvariant(), open, fingerprint, clientOrderId, state, etNow);
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		// Several processes write this file (watch, scan, `wa trade`): a lost line here could let an order through twice.
		IO.SharedFileAppender.AppendLine(_path, JsonSerializer.Serialize(entry));
	}

	/// <summary>Today's live entries (ET date match), one per order: an order's latest line decides its state, and voided orders are dropped.
	/// The entry keeps its first line's <see cref="Entry.At"/>, so it says when the order was sent. Lines without a client_order_id are each
	/// their own order. Corrupt lines are skipped — a torn concurrent append must not take down the refresh that's protecting live submission.</summary>
	public IReadOnlyList<Entry> ReadFor(DateTime etDate)
	{
		if (!File.Exists(_path)) return Array.Empty<Entry>();
		var date = etDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var entries = new List<Entry>();
		var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var line in IO.SharedFileReader.ReadAllLines(_path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			Entry? e = null;
			try { e = JsonSerializer.Deserialize<Entry>(line); } catch (JsonException) { }
			if (e == null || e.Date != date) continue;
			if (string.IsNullOrEmpty(e.ClientOrderId)) { entries.Add(e); continue; }
			if (indexById.TryGetValue(e.ClientOrderId, out var i)) entries[i] = entries[i] with { State = e.State };
			else { indexById[e.ClientOrderId] = entries.Count; entries.Add(e); }
		}
		return entries.Where(e => e.State != Void).ToList();
	}
}
