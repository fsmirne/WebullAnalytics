using System.Globalization;
using WebullAnalytics.Pricing;

namespace WebullAnalytics;

internal enum AsOfKind { Now, Fill, Instant }

/// <summary>
/// The value of --date: the instant (ET) a command evaluates at. <c>now</c> is the live market; <c>fill</c> is the instant of the
/// --fill being analyzed; <c>YYYY-MM-DD</c> is that session at <see cref="DefaultTime"/>; <c>YYYY-MM-DDTHH:MM</c> (or a quoted
/// <c>"YYYY-MM-DD HH:MM"</c>) is that exact minute. A date alone always means 10:00 ET — a fixed, deterministic minute past the opening
/// auction's noise, inside every session including half-days. The time of an instant must fall inside that day's regular session.
/// </summary>
internal readonly record struct AsOf(AsOfKind Kind, DateTime Et)
{
	internal static readonly TimeSpan DefaultTime = new(10, 0, 0);

	internal const string Help = "Evaluation instant (ET): 'now' (default), 'fill' (the --fill's own instant), YYYY-MM-DD (that session at 10:00 ET), or YYYY-MM-DDTHH:MM (that minute, inside the regular session). A past instant reads the market from the quote store / ThetaData at exactly that minute; a future one projects the live market forward to it.";

	internal static readonly AsOf Now = new(AsOfKind.Now, default);

	private static readonly string[] InstantFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm"];

	/// <summary>The pinned instant, or null for <c>now</c> and <c>fill</c> (the fill's instant is known only once it is picked).</summary>
	internal DateTime? Instant => Kind == AsOfKind.Instant ? Et : null;

	internal bool IsPast => Kind == AsOfKind.Fill || (Kind == AsOfKind.Instant && Et < MarketTime.NowEt());

	internal static bool TryParse(string? text, out AsOf value, out string? error)
	{
		value = Now;
		error = null;
		var s = text?.Trim() ?? "";
		if (s.Length == 0 || s.Equals("now", StringComparison.OrdinalIgnoreCase)) return true;
		if (s.Equals("fill", StringComparison.OrdinalIgnoreCase)) { value = new(AsOfKind.Fill, default); return true; }

		DateTime et;
		if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) et = date + DefaultTime;
		else if (!DateTime.TryParseExact(s, InstantFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out et))
		{
			error = $"--date: expected now, fill, YYYY-MM-DD or YYYY-MM-DDTHH:MM, got '{text}'";
			return false;
		}
		var close = MarketCalendar.CloseTimeEt(et.Date);
		if (et.TimeOfDay < OptionMath.MarketOpen || et.TimeOfDay > close)
		{
			error = $"--date: {et:HH:mm} is outside the {et:yyyy-MM-dd} regular session (09:30–{close:hh\\:mm} ET)";
			return false;
		}
		value = new(AsOfKind.Instant, et);
		return true;
	}

	/// <summary>Validates a --date value for a command: <c>fill</c> only where a --fill is being analyzed, and no --spot at a past
	/// instant (its spot is the stored market's). Null when valid.</summary>
	internal static string? Validate(string? text, bool fillAllowed, string? spot)
	{
		if (!TryParse(text, out var value, out var error)) return error;
		if (value.Kind == AsOfKind.Fill && !fillAllowed) return "--date fill needs --fill";
		if (value.IsPast && !string.IsNullOrEmpty(spot)) return "--spot doesn't apply at a past --date: the spot is the stored market's at that instant";
		return null;
	}

	/// <summary>Parses a value that already passed <see cref="Validate"/>.</summary>
	internal static AsOf Parse(string? text) => TryParse(text, out var value, out var error) ? value : throw new FormatException(error);

	public override string ToString() => Kind switch { AsOfKind.Now => "now", AsOfKind.Fill => "fill", _ => Et.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " ET" };
}
