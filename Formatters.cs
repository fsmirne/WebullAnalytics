using Spectre.Console;
using System.Globalization;

namespace WebullAnalytics;

/// <summary>
/// Formatting utilities for displaying quantities, prices, and dates.
/// </summary>
public static class Formatters
{
	/// <summary>
	/// Formats a quantity, removing unnecessary trailing zeros.
	/// </summary>
	public static string FormatQty(decimal qty)
	{
		var text = qty.ToString("F", CultureInfo.InvariantCulture);
		return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
	}

	/// <summary>
	/// Formats a price with appropriate decimal places.
	/// Options use 3 decimals, stocks use 2. Trailing zeros are trimmed but at least 2 decimals are kept.
	/// </summary>
	public static string FormatPrice(decimal value, Asset asset)
	{
		var decimals = asset is Asset.Option or Asset.OptionStrategy ? 3 : 2;
		var text = value.ToString($"F{decimals}", CultureInfo.InvariantCulture).TrimEnd('0');

		var dot = text.IndexOf('.');
		if (dot < 0)
			return text + ".00";

		var decimalLen = text.Length - dot - 1;
		return decimalLen switch
		{
			0 => text + "00",
			1 => text + "0",
			_ => text
		};
	}

	/// <summary>
	/// Formats a P&L value with color (green for positive, red for negative).
	/// </summary>
	public static Markup FormatPnL(decimal value)
	{
		var color = value >= 0 ? "green" : "red";
		var text = value.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
		return new Markup($"[{color}]{text}[/]");
	}

	/// <summary>
	/// Formats a money value with color (green if above initial, red if below).
	/// </summary>
	public static Markup FormatMoney(decimal value, decimal initialAmount)
	{
		var color = value >= initialAmount ? "green" : "red";
		var text = value.ToString("$#,##0.00", CultureInfo.InvariantCulture);
		return new Markup($"[{color}]{text}[/]");
	}

	/// <summary>
	/// Formats an optional expiry date, returning "-" if null.
	/// </summary>
	public static string FormatExpiry(DateTime? expiry) =>
		expiry.HasValue ? FormatOptionDate(expiry.Value) : "-";

	/// <summary>
	/// Formats a date for option display (e.g., "13 Feb 2026").
	/// </summary>
	public static string FormatOptionDate(DateTime date) =>
		date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

	/// <summary>
	/// Formats an option for display (e.g., "GME 13 Feb 2026 $25").
	/// </summary>
	public static string FormatOptionDisplay(string root, DateTime expiryDate, decimal strike) =>
		$"{root} {FormatOptionDate(expiryDate)} ${FormatQty(strike)}";
}
