using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.Analyze;

/// <summary>Single owner of the per-day reading log <c>data/cascade/&lt;TICKER&gt;/&lt;ET date&gt;.jsonl</c>: one row per live
/// `analyze cascade` run, holding the whole <see cref="CascadeReading"/> the panel was rendered from (regime, every setup's
/// score, factors and notes, the terrain and the inputs), so what the panel showed at the time survives the run. Past
/// --date replays are not logged: they are reproducible from the stores and would interleave hypothetical rows with live ones.
/// <c>runEt</c> is the wall clock of the run and <c>asOfEt</c> the instant evaluated (equal on every logged row, since a --date run is a replay), and
/// <c>spotOverridden</c> marks a --spot run — both are hypothetical readings a study of live rows should drop.</summary>
internal static class CascadeLog
{
	internal static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

	internal static void Append(string ticker, string vendor, DateTime runEt, DateTime asOfEt, bool spotOverridden, CascadeReading reading)
	{
		var path = Program.ResolvePath($"data/cascade/{ticker}/{runEt:yyyy-MM-dd}.jsonl");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		WebullAnalytics.IO.SharedFileAppender.AppendLine(path, Serialize(ticker, vendor, runEt, asOfEt, spotOverridden, reading));
	}

	internal static string Serialize(string ticker, string vendor, DateTime runEt, DateTime asOfEt, bool spotOverridden, CascadeReading reading)
		=> JsonSerializer.Serialize(new { type = "analyze_cascade", runEt = runEt.ToString("yyyy-MM-ddTHH:mm:ss"), asOfEt = asOfEt.ToString("yyyy-MM-ddTHH:mm:ss"), ticker, vendor, spotOverridden, reading }, Options);
}
