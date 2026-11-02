using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.Pricing.Clock;

/// <summary>One out-of-the-money option's two-sided book.</summary>
internal readonly record struct ClockQuote(decimal Strike, bool IsCall, decimal Bid, decimal Ask)
{
	[JsonIgnore] internal decimal Mid => (Bid + Ask) / 2m;
}

/// <summary>One expiry's book at one instant: the put-call-parity forward and the out-of-the-money options around it.</summary>
internal sealed record ClockExpiryBook(DateOnly Expiry, decimal Forward, IReadOnlyList<ClockQuote> Options);

/// <summary>One trading day of calibration books for a root: the 16:00 closing book (<see cref="Close"/>) and the 09:45 book
/// (<see cref="Open"/>). A night's repricing pairs one day's <see cref="Close"/> with the next trading day's <see cref="Open"/>.
/// Either side may be empty (a source gap); the fitter just skips the nights it can't pair.</summary>
internal sealed record ClockDay(DateOnly Date, IReadOnlyList<ClockExpiryBook> Close, IReadOnlyList<ClockExpiryBook> Open);

/// <summary>The trading-time clock's calibration store, <c>data/clock/{ROOT}/{yyyy-MM-dd}.json.gz</c> — the app's own
/// small record of close/open books, independent of quotes.db (which only seeds it). Gzipped JSON, one file per day.</summary>
internal static class ClockStore
{
	/// <summary>Instants the books represent, ET: the session's last NBBO at 16:00 and the NBBO at 09:46. quotes.db labels a minute
	/// by its START (ThetaData's end-of-minute stamp shifted −60s), so those are its 15:59 and 09:45 rows.</summary>
	internal static readonly TimeOnly CloseTime = new(16, 0);
	internal static readonly TimeOnly OpenTime = new(9, 46);

	/// <summary>Strike band kept around the forward, and the longest expiry kept, in calendar days from the book's date.</summary>
	internal const decimal Band = 0.05m;
	internal const int MaxDte = 90;

	private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

	internal static string RootDirectory(string root) => Program.ResolvePath($"data/clock/{root.ToUpperInvariant()}");

	private static string PathFor(string root, DateOnly date) => Path.Combine(RootDirectory(root), $"{date:yyyy-MM-dd}.json.gz");

	internal static bool Has(string root, DateOnly date) => File.Exists(PathFor(root, date));

	/// <summary>Every stored date for <paramref name="root"/>, ascending.</summary>
	internal static IReadOnlyList<DateOnly> Dates(string root)
	{
		var dir = RootDirectory(root);
		if (!Directory.Exists(dir)) return [];
		return Directory.EnumerateFiles(dir, "*.json.gz")
			.Select(f => DateOnly.TryParseExact(Path.GetFileName(f)[..10], "yyyy-MM-dd", out var d) ? d : (DateOnly?)null)
			.Where(d => d.HasValue).Select(d => d!.Value).OrderBy(d => d).ToList();
	}

	internal static ClockDay? Load(string root, DateOnly date)
	{
		var path = PathFor(root, date);
		if (!File.Exists(path)) return null;
		using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
		return JsonSerializer.Deserialize<ClockDay>(gz, Json);
	}

	internal static void Save(string root, ClockDay day)
	{
		var path = PathFor(root, day.Date);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var tmp = path + ".tmp";
		using (var gz = new GZipStream(File.Create(tmp), CompressionLevel.SmallestSize))
			JsonSerializer.Serialize(gz, day, Json);
		File.Move(tmp, path, overwrite: true);
	}

	/// <summary>Builds one expiry's stored book from raw two-sided quotes (strike, isCall, bid, ask): the forward from put-call parity
	/// on the three strikes where |C − P| is smallest (median of K + C − P), then the out-of-the-money options within
	/// <see cref="Band"/> of it. Null when parity can't be formed (fewer than three strikes quoted on both sides).</summary>
	internal static ClockExpiryBook? BuildExpiryBook(DateOnly expiry, IReadOnlyList<(decimal Strike, bool IsCall, decimal Bid, decimal Ask)> quotes)
	{
		var two = quotes.Where(q => q.Bid > 0m && q.Ask > 0m && q.Ask >= q.Bid).ToList();
		var calls = two.Where(q => q.IsCall).GroupBy(q => q.Strike).ToDictionary(g => g.Key, g => (g.First().Bid + g.First().Ask) / 2m);
		var puts = two.Where(q => !q.IsCall).GroupBy(q => q.Strike).ToDictionary(g => g.Key, g => (g.First().Bid + g.First().Ask) / 2m);
		var parity = calls.Keys.Intersect(puts.Keys).OrderBy(k => Math.Abs(calls[k] - puts[k])).Take(3).Select(k => k + calls[k] - puts[k]).OrderBy(f => f).ToList();
		if (parity.Count < 3) return null;
		var forward = parity[1];
		var otm = two.Where(q => Math.Abs(q.Strike / forward - 1m) <= Band && q.IsCall == (q.Strike >= forward))
			.GroupBy(q => (q.Strike, q.IsCall)).Select(g => g.First())
			.OrderBy(q => q.Strike).Select(q => new ClockQuote(q.Strike, q.IsCall, q.Bid, q.Ask)).ToList();
		return otm.Count == 0 ? null : new ClockExpiryBook(expiry, forward, otm);
	}
}
