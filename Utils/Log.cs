namespace WebullAnalytics.Utils;

internal enum LogVerbosity { Error, Information, Debug }

/// <summary>Process-wide console verbosity gate. Low-level layers (e.g. the Webull client) route progress
/// chatter through <see cref="Debug"/> so it only surfaces at debug verbosity; by default (Information) it is
/// suppressed, keeping <c>report</c> / <c>ai scan</c> / <c>ai watch</c> output clean. <see cref="Level"/> is
/// set once at command startup — AI commands set it from <c>AIConfig.LogLevel</c> via <see cref="Parse"/>;
/// other commands keep the Information default. Debug chatter goes to stderr so it never mixes into the
/// stdout report/data stream.</summary>
internal static class Log
{
	internal static LogVerbosity Level { get; set; } = LogVerbosity.Information;

	internal static bool IsDebug => Level == LogVerbosity.Debug;

	/// <summary>Diagnostic progress chatter — written to stderr only at debug verbosity.</summary>
	internal static void Debug(string message)
	{
		if (Level == LogVerbosity.Debug) Console.Error.WriteLine(message);
	}

	internal static LogVerbosity Parse(string? level) => level?.Trim().ToLowerInvariant() switch
	{
		"debug" => LogVerbosity.Debug,
		"error" => LogVerbosity.Error,
		_ => LogVerbosity.Information,
	};
}
