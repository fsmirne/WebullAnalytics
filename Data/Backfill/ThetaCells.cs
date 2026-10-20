using System.Globalization;
using WebullAnalytics.Api.ThetaData;

namespace WebullAnalytics.Data.Backfill;

/// <summary>Cell readers shared by the backfill pulls — the Python backfill's schema-tolerant helpers (pick_col, norm_date,
/// norm_right) over a <see cref="ThetaTable"/>.</summary>
internal static class ThetaCells
{
	/// <summary>The first of <paramref name="candidates"/> the table has (pick_col).</summary>
	internal static int Pick(ThetaTable table, params string[] candidates) =>
		candidates.Select(table.ColumnOrNull).FirstOrDefault(c => c != null) ?? throw new KeyNotFoundException($"none of [{string.Join(", ", candidates)}] in columns [{string.Join(", ", table.Headers)}]");

	/// <summary>A date cell as a calendar date (norm_date): a timestamp's own wall-clock date, or the leading yyyy-MM-dd / yyyyMMdd of
	/// text or a number.</summary>
	internal static DateOnly Date(ThetaTable table, int row, int column) => table.Value(row, column) switch
	{
		DateTimeOffset t => DateOnly.FromDateTime(t.DateTime),
		long n => DateOnly.ParseExact(n.ToString(CultureInfo.InvariantCulture)[..8], "yyyyMMdd", CultureInfo.InvariantCulture),
		var other => ParseDateText(Convert.ToString(other, CultureInfo.InvariantCulture) ?? "")
	};

	private static DateOnly ParseDateText(string text)
	{
		var digits = new string(text.Where(c => c is not ('-' or '/')).Take(8).ToArray());
		return DateOnly.ParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture);
	}

	/// <summary>'C' or 'P' from the vendor's right text (CALL / PUT); anything else is a schema surprise worth failing on.</summary>
	internal static char Right(ThetaTable table, int row, int column)
	{
		var text = table.Text(row, column).Trim().ToUpperInvariant();
		return text.StartsWith('C') ? 'C' : text.StartsWith('P') ? 'P' : throw new FormatException($"unrecognized right value: '{text}'");
	}

	/// <summary>An integer-valued numeric cell (sizes, volume, print counts) the way the Python writer read it: truncated toward
	/// zero, 0 when absent.</summary>
	internal static long Count(ThetaTable table, int row, int column)
	{
		var value = table.Double(row, column);
		return double.IsNaN(value) ? 0 : (long)value;
	}
}
