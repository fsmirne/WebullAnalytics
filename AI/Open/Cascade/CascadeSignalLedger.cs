using System.Text.Json;
using System.Text.Json.Serialization;
using WebullAnalytics.Analyze;

namespace WebullAnalytics.AI.Open.Cascade;

/// <summary>
/// What became of each cascade setup's first confirmation of the day (<see cref="CascadeOpener"/>), and which regimes confirmed at all
/// (<see cref="Confirmed"/> — what the neutral setup waits on: it trades only on a day nothing has confirmed). A setup trades once a day, so the
/// ledger is what stops a watch loop from re-entering after its trailing stop closed the position, after a restart, or from a
/// second process. A confirmation that builds a trade writes <see cref="Signaled"/>, then <see cref="Placed"/> once the auto-executor
/// acted on it; one that builds none writes <see cref="Skipped"/>. Placed and Skipped are final; a setup that is only Signaled may be
/// proposed again until its retry window ends. Append-only JSONL shared by every process on the machine. A dry run and a live run
/// keep separate books: a morning dry run must not use up the signal a later --submit run trades. Without a path (a backtest)
/// the entries live in memory and die with the run.
/// </summary>
internal sealed class CascadeSignalLedger
{
	/// <param name="Outcome"><see cref="Signaled"/>, <see cref="Placed"/> or <see cref="Skipped"/>; <paramref name="Detail"/> is the trade's legs, or why there was none.</param>
	internal sealed record Entry(
		[property: JsonPropertyName("date")] string Date,
		[property: JsonPropertyName("ticker")] string Ticker,
		[property: JsonPropertyName("setup")] CascadeRegime Setup,
		[property: JsonPropertyName("timeEt")] string TimeEt,
		[property: JsonPropertyName("live")] bool Live,
		[property: JsonPropertyName("score")] int Score,
		[property: JsonPropertyName("spot")] decimal Spot,
		[property: JsonPropertyName("outcome")] string Outcome,
		[property: JsonPropertyName("detail")] string Detail,
		// The neutral setup's two trades ("call" / "put") are tracked apart; null for a trap or cascade setup.
		[property: JsonPropertyName("side")] string? Side = null);

	/// <summary>A trade was built and handed to the auto-executor; not final until it is <see cref="Placed"/>.</summary>
	internal const string Signaled = "signaled";
	/// <summary>The auto-executor acted on the trade: an order placed, or logged by a dry run.</summary>
	internal const string Placed = "placed";
	/// <summary>The first confirmation produced no trade.</summary>
	internal const string Skipped = "skipped";
	/// <summary>The regime confirmed for the first time today, whether or not its setup is enabled; a market fact, so it counts in every mode.</summary>
	internal const string Confirmed = "confirmed";

	private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

	private readonly string? _path;
	private readonly List<Entry> _memory = new();

	public CascadeSignalLedger(string? path = null) => _path = path;

	internal static CascadeSignalLedger Default() => new(Program.ResolvePath("data/cascade/signals.jsonl"));

	/// <summary>This setup's (and side's) trade entries for the day in one mode (live or dry run), oldest first; confirmations are not included.</summary>
	public IReadOnlyList<Entry> Find(DateOnly date, string ticker, CascadeRegime setup, bool live, string? side = null)
	{
		var day = date.ToString("yyyy-MM-dd");
		return All().Where(e => e.Date == day && e.Setup == setup && e.Live == live && e.Side == side && e.Outcome != Confirmed && string.Equals(e.Ticker, ticker, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	/// <summary>The regimes recorded as confirmed for the day, in any mode.</summary>
	public IReadOnlySet<CascadeRegime> ConfirmedRegimes(DateOnly date, string ticker)
	{
		var day = date.ToString("yyyy-MM-dd");
		return All().Where(e => e.Date == day && e.Outcome == Confirmed && string.Equals(e.Ticker, ticker, StringComparison.OrdinalIgnoreCase)).Select(e => e.Setup).ToHashSet();
	}

	/// <summary>Every entry, oldest first.</summary>
	public IReadOnlyList<Entry> Entries => All().ToList();

	/// <summary>Every readable entry, oldest first; a torn line is skipped.</summary>
	private IEnumerable<Entry> All()
	{
		if (_path == null) return _memory;
		if (!File.Exists(_path)) return Array.Empty<Entry>();
		var entries = new List<Entry>();
		foreach (var line in File.ReadLines(_path))
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			try { if (JsonSerializer.Deserialize<Entry>(line, Options) is { } e) entries.Add(e); } catch (JsonException) { }
		}
		return entries;
	}

	public void Append(Entry entry)
	{
		if (_path == null) { _memory.Add(entry); return; }
		var dir = Path.GetDirectoryName(_path);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		WebullAnalytics.IO.SharedFileAppender.AppendLine(_path, JsonSerializer.Serialize(entry, Options));
	}
}
