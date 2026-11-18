using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

/// <summary>
/// The stop orders <see cref="BrokerStopManager"/> placed, so it only ever moves or cancels its own: a stop the user put on a position by hand
/// (the app has trailing stops the API cannot see) is never touched. A small JSON file, shared by every process on the machine; each write
/// re-reads it first so two processes appending do not drop each other's entries.
/// </summary>
internal sealed class BrokerStopStore
{
	internal sealed record Entry(
		[property: JsonPropertyName("accountId")] string AccountId,
		[property: JsonPropertyName("clientOrderId")] string ClientOrderId,
		[property: JsonPropertyName("fingerprint")] string Fingerprint,
		[property: JsonPropertyName("placedAt")] DateTime PlacedAt);

	/// <summary>A GTC stop can guard a position for a couple of weeks; an entry older than this belongs to one that is long gone.</summary>
	private static readonly TimeSpan Retention = TimeSpan.FromDays(60);

	private readonly string? _path;
	private List<Entry> _entries;

	public BrokerStopStore(string? path = null)
	{
		_path = path;
		_entries = ReadFile();
	}

	internal static BrokerStopStore Default() => new(Program.ResolvePath("data/broker-stops.json"));

	/// <summary>Records a stop this process placed. <paramref name="fingerprint"/> is that of the order's legs (see <see cref="BrokerStateService.Fingerprint"/>).</summary>
	public void Add(string accountId, string clientOrderId, string fingerprint, DateTime now)
	{
		_entries = ReadFile();
		_entries.RemoveAll(e => e.ClientOrderId == clientOrderId || now - e.PlacedAt > Retention);
		_entries.Add(new Entry(accountId, clientOrderId, fingerprint, now));
		WriteFile();
	}

	public void Remove(string clientOrderId)
	{
		_entries = ReadFile();
		if (_entries.RemoveAll(e => e.ClientOrderId == clientOrderId) > 0) WriteFile();
	}

	/// <summary>True when <paramref name="clientOrderId"/> is a stop this manager placed on <paramref name="accountId"/>.</summary>
	public bool Owns(string accountId, string? clientOrderId) =>
		clientOrderId != null && ReadFile().Any(e => e.AccountId == accountId && e.ClientOrderId == clientOrderId);

	/// <summary>The newest stop placed on <paramref name="fingerprint"/> no longer ago than <paramref name="within"/>: it may not be listed by the broker yet
	/// (the order endpoints lag), and must not be taken for "no stop" and placed twice.</summary>
	public Entry? Recent(string accountId, string fingerprint, DateTime now, TimeSpan within) =>
		ReadFile().Where(e => e.AccountId == accountId && e.Fingerprint == fingerprint && now - e.PlacedAt <= within).OrderByDescending(e => e.PlacedAt).FirstOrDefault();

	private List<Entry> ReadFile()
	{
		if (_path == null) return _entries ?? new List<Entry>();
		if (!File.Exists(_path)) return new List<Entry>();
		try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new List<Entry>(); }
		catch (Exception ex) when (ex is JsonException or IOException) { return new List<Entry>(); }
	}

	private void WriteFile()
	{
		if (_path == null) return;
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
	}
}
