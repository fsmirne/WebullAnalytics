using System.Globalization;
using System.Text.RegularExpressions;

namespace WebullAnalytics.Clipboard;

/// <summary>One OCR'd word with its position: X/Y is the bounding-box top-left, H its height. Y-center
/// (Y + H/2) is what row clustering uses.</summary>
internal readonly record struct OcrWord(string Text, double X, double Y, double H);

internal sealed record TicketLeg(string Symbol, decimal Strike, DateTime Expiry, string CallPut, string Action, int Qty)
{
	/// <summary>OCC symbol, e.g. GME260724P00021500.</summary>
	public string OccSymbol => $"{Symbol}{Expiry:yyMMdd}{CallPut}{(long)(Strike * 1000m):00000000}";
}

internal sealed record TicketParse(IReadOnlyList<TicketLeg> Legs, decimal? NetLimit, string Tif, IReadOnlyList<string> Problems, IReadOnlyList<string> RowTexts);

/// <summary>Parses an OCR'd Webull multi-leg order ticket (the order-entry rows: one header row plus one
/// "Leg N" row per leg) into legs + net limit + TIF. OCR text is noisy — digits split by stray spaces
/// ("1 ,500", "21.5/21 .5"), dropped currency signs — so every numeric regex tolerates embedded spaces.
///
/// The ticket repeats strikes/expiries/qty/type between the header and the leg rows; <see cref="Parse"/>
/// cross-checks them and reports mismatches as Problems. A single OCR misread therefore surfaces as a
/// visible failure instead of silently producing a wrong `wa trade place` line — callers must show
/// Problems prominently and treat the commands as untrustworthy when any exist.</summary>
internal static class WebullTicketParser
{
	private static readonly Regex LegRx = new(
		@"^Leg\s*\d+\s+(?<sym>[A-Z]{1,6})\s+(?<strike>\d+(?:\s*\.\s*\d+)?)\s+(?<d>\d{1,2})\s+(?<mon>[A-Za-z]{3})\s+(?<yr>\d{2})\W*(?:\(\s*\w+\s*\))?\s+(?<cp>Call|Put)\s+(?<side>Buy|Sell)\s+(?<qty>\d(?:[ ,]*\d)*)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>Clusters words into visual rows by Y-center (tolerance = 0.7 × average word height, which
	/// separates adjacent table rows while absorbing per-word baseline jitter), then joins each row's words
	/// left-to-right.</summary>
	internal static IReadOnlyList<string> ClusterRows(IReadOnlyList<OcrWord> words)
	{
		if (words.Count == 0) return [];
		var tol = words.Average(w => w.H) * 0.7;
		var rows = new List<(double Y, List<OcrWord> Words)>();
		foreach (var w in words.OrderBy(w => w.Y + w.H / 2).ThenBy(w => w.X))
		{
			var yc = w.Y + w.H / 2;
			var row = rows.FirstOrDefault(r => Math.Abs(r.Y - yc) <= tol);
			if (row.Words != null) row.Words.Add(w);
			else rows.Add((yc, [w]));
		}
		return rows.Select(r => string.Join(' ', r.Words.OrderBy(w => w.X).Select(w => w.Text).Where(IsRealToken))).ToList();
	}

	/// <summary>Drops OCR junk tokens: the ticket's dropdown arrows, edit icons and cell borders come out as
	/// stray single characters ("|", "»", "*", "J", "y") after inversion. Nothing in the ticket vocabulary is
	/// a legitimate single NON-DIGIT character (sides/types are full words, strikes/qtys are digits), so any
	/// such token is noise.</summary>
	private static bool IsRealToken(string t) => t.Length > 1 || (t.Length == 1 && char.IsDigit(t[0]));

	internal static TicketParse Parse(IReadOnlyList<string> rowTexts)
	{
		var problems = new List<string>();
		var legs = new List<TicketLeg>();
		foreach (var t in rowTexts)
		{
			var m = LegRx.Match(t);
			if (!m.Success) continue;
			DateTime expiry;
			try
			{
				expiry = DateTime.ParseExact($"{m.Groups["d"].Value} {m.Groups["mon"].Value} {m.Groups["yr"].Value}", "d MMM yy", CultureInfo.InvariantCulture);
			}
			catch (FormatException) { problems.Add($"unparseable expiry in row: {t}"); continue; }
			legs.Add(new TicketLeg(
				m.Groups["sym"].Value.ToUpperInvariant(),
				decimal.Parse(m.Groups["strike"].Value.Replace(" ", ""), CultureInfo.InvariantCulture),
				expiry,
				m.Groups["cp"].Value[..1].ToUpperInvariant(),
				m.Groups["side"].Value.ToLowerInvariant(),
				int.Parse(Regex.Replace(m.Groups["qty"].Value, "[ ,]", ""), CultureInfo.InvariantCulture)));
		}

		// Header = the value row (has an actual $ amount), never the column-title row (which also says "Limit").
		var header = rowTexts.FirstOrDefault(t => !Regex.IsMatch(t, @"^Leg\s", RegexOptions.IgnoreCase) && Regex.IsMatch(t, @"\$\s*\d"));
		decimal? limit = null;
		var tif = "day";
		if (header == null)
		{
			problems.Add("no header row with a $ limit price found");
		}
		else
		{
			var lm = Regex.Match(header, @"\$\s*(\d+(?:\s*\.\s*\d+)?)");
			if (lm.Success) limit = decimal.Parse(lm.Groups[1].Value.Replace(" ", ""), CultureInfo.InvariantCulture);
			if (Regex.IsMatch(header, @"\bGTC\b", RegexOptions.IgnoreCase)) tif = "gtc";

			// Cross-checks against the redundant header fields.
			var hs = Regex.Match(header, @"(\d+(?:\s*\.\s*\d+)?)\s*/\s*(\d+(?:\s*\.\s*\d+)?)");
			if (hs.Success && legs.Count == 2)
			{
				var hdr = new[] { decimal.Parse(hs.Groups[1].Value.Replace(" ", ""), CultureInfo.InvariantCulture), decimal.Parse(hs.Groups[2].Value.Replace(" ", ""), CultureInfo.InvariantCulture) }.OrderBy(x => x).ToArray();
				var leg = legs.Select(l => l.Strike).OrderBy(x => x).ToArray();
				if (hdr[0] != leg[0] || hdr[1] != leg[1]) problems.Add($"header strikes {hdr[0]}/{hdr[1]} != leg strikes {leg[0]}/{leg[1]}");
			}
			var ht = Regex.Match(header, @"\b(Call|Put)\b", RegexOptions.IgnoreCase);
			if (ht.Success)
			{
				var hcp = ht.Groups[1].Value[..1].ToUpperInvariant();
				foreach (var l in legs.Where(l => l.CallPut != hcp)) problems.Add($"header type {hcp} != leg type {l.CallPut} ({l.OccSymbol})");
			}
			var hq = Regex.Match(header, @"(?:Buy|Sell)\s+(\d(?:[ ,]*\d)*)\s+(?:Limit|Market)\b", RegexOptions.IgnoreCase);
			if (hq.Success)
			{
				var qty = int.Parse(Regex.Replace(hq.Groups[1].Value, "[ ,]", ""), CultureInfo.InvariantCulture);
				foreach (var l in legs.Where(l => l.Qty != qty)) problems.Add($"header qty {qty} != leg qty {l.Qty} ({l.OccSymbol})");
			}
		}

		if (legs.Count == 0) problems.Add("no leg rows recognized");
		if (limit == null) problems.Add("no limit price found in header");
		if (legs.Select(l => l.Symbol).Distinct().Count() > 1) problems.Add("legs disagree on the underlying symbol");

		return new TicketParse(legs, limit, tif, problems, rowTexts);
	}
}
