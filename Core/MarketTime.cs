namespace WebullAnalytics;

/// <summary>The exchange time zone (America/New_York, DST-aware) and the conversions between it and UTC — the single place every
/// ET-aware path resolves the zone, so no caller looks it up or re-derives a conversion idiom on its own.
/// Storage conventions to keep straight when reading raw files: the intraday tape CSVs (data/intraday/{TICKER}/*.csv) stamp bars
/// in UTC (header <c>timestamp_utc</c>, trailing Z), while quotes.db's <c>date</c>/<c>time_sec</c> are ET wall-clock
/// (see <see cref="QuoteRow"/>).</summary>
internal static class MarketTime
{
	internal static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	/// <summary>The instant expressed in ET (its offset becomes ET's at that instant).</summary>
	internal static DateTimeOffset ToEt(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

	/// <summary>ET wall-clock for <paramref name="instant"/>, honoring its Kind (Utc, or Local/Unspecified read as machine-local) —
	/// <see cref="TimeZoneInfo.ConvertTime(DateTime, TimeZoneInfo)"/> semantics.</summary>
	internal static DateTime ToEt(DateTime instant) => TimeZoneInfo.ConvertTime(instant, Zone);

	/// <summary>ET wall-clock for a UTC <paramref name="utc"/> (Unspecified is read as UTC) —
	/// <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> semantics.</summary>
	internal static DateTime UtcToEt(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);

	/// <summary>The UTC instant (Kind=Utc) of an ET wall-clock time. The Kind of <paramref name="etWallClock"/> is ignored — it is
	/// always read as ET — which also avoids ConvertTimeToUtc rejecting a Kind=Local/Utc input against a non-local zone.</summary>
	internal static DateTime EtToUtc(DateTime etWallClock) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etWallClock, DateTimeKind.Unspecified), Zone);

	/// <summary><see cref="EtToUtc"/> as a zero-offset <see cref="DateTimeOffset"/>.</summary>
	internal static DateTimeOffset EtToInstant(DateTime etWallClock) => new(EtToUtc(etWallClock), TimeSpan.Zero);

	/// <summary>The current ET wall-clock time.</summary>
	internal static DateTime NowEt() => ToEt(DateTime.UtcNow);

	/// <summary>The current ET calendar date.</summary>
	internal static DateTime TodayEt() => NowEt().Date;
}
