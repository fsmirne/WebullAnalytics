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

internal sealed record TicketParse(IReadOnlyList<TicketLeg> Legs, decimal? NetLimit, string Tif, IReadOnlyList<string> Problems, IReadOnlyList<string> RowTexts, int? HeaderQty = null)
{
	/// <summary>Non-fatal notices (e.g. a reconstructed leg). Shown prominently but do NOT suppress the place line.</summary>
	public IReadOnlyList<string> Warnings { get; init; } = [];

	/// <summary>The header row's strike field as read by THIS pass — tokens joined by "/" with OCR spaces
	/// stripped, null when the pass found no header row or no field in it. <see cref="WebullTicketParser.Merge"/>
	/// votes over the passes' readings instead of trusting any single one.</summary>
	public string? HeaderStrikeField { get; init; }
}

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
	/// <summary>One "Leg N" row. Two tolerances are OCR-driven, not cosmetic: the expiry's separators are
	/// OPTIONAL because the max-channel passes read the date cell glued ("15Jan27" for "15 Jan 27") — those are
	/// the only passes that see Webull's RED "Sell" word, so requiring spaces there silently lost every short
	/// leg of a ticket — and a 1-2 letter token is allowed between the strike and the expiry because the row's
	/// dropdown caret OCRs as one ("110 vy 15 Jan 27"). Both are strictly narrower than the field they guard:
	/// no side, type or quantity is ever inferred.</summary>
	private static readonly Regex LegRx = new(
		@"^Leg\s*\d+\s+(?<sym>[A-Z]{1,6})\s+[^\d]{0,2}(?<strike>\d+(?:\s*\.\s*\d+)?)(?:\s+[A-Za-z]{1,2})?\s+(?<d>\d{1,2})\s*(?<mon>[A-Za-z]{3})\s*(?<yr>\d{2})\W*(?:\(\s*\w+\s*\))?\s+(?<cp>Call|Put)\s+(?<side>Buy|Sell)\s+[^\d]{0,3}(?<qty>\d(?:[ ,]*\d)*)",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>The header row's strike field: one slash-separated token per leg ("20.5/21.5/21.5/23").
	/// Spaces are tolerated around the decimal point, around the slashes, and INSIDE a token's integer digits —
	/// the cramped cell makes tesseract break a token in two ("100/110/120/130" reads as "100/1 10/1 20/1 30"),
	/// and stopping at the first break truncated the field to "100/1". Absorption ends where the next digit
	/// group is followed by letters: that group is the expiration column's day ("... /130 15 Jan 27"), the one
	/// number in the row that must never be read as a strike. The (?!\d) is load-bearing — without it the digit
	/// run backtracks a character at a time until the letters fall out of the guard's reach and absorbs "3" of
	/// the "31 Jul" day.</summary>
	private static readonly Regex HeaderStrikeFieldRx = new(@"\d+(?:[ ,]+\d+(?!\d)(?![ ,]*[A-Za-z]))*(?:\s*\.\s*\d+)?(?:\s*/\s*\d+(?:[ ,]+\d+(?!\d)(?![ ,]*[A-Za-z]))*(?:\s*\.\s*\d+)?)+", RegexOptions.Compiled);

	/// <summary>Digit sequence of a number — decimal point and OCR spaces dropped. Strike cross-checks compare
	/// these rather than parsed values: tesseract's known failure mode on the cramped header cell is losing the
	/// decimal point ("21.5" reads as "215"), which this forgives while still catching real mismatches.</summary>
	private static string Digits(string s) => Regex.Replace(s, @"[^\d]", "");

	/// <summary>Splits a strike field into its per-leg tokens, dropping the spaces and thousands separators OCR
	/// scatters inside them.</summary>
	private static List<string> FieldTokens(string field) => field.Split('/').Select(t => t.Replace(" ", "").Replace(",", "")).Where(t => t.Length > 0).ToList();

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

	/// <summary>Merges the parses from a multi-scale OCR ensemble. Each upscale factor misreads DIFFERENT
	/// rows (one drops a red "Sell", another loses a quantity), so the union across passes recovers legs no
	/// single pass sees. Legs are keyed by contract+side; a conflicting quantity for the same contract is a
	/// Problem, never a guess. Every header field is decided by a VOTE over the passes' readings — never by
	/// whichever pass happened to come first, since each pass is an equally noisy look at the same cell — and
	/// per-pass strike arity problems are re-derived against the MERGED leg set (a pass that saw only 2 of 4
	/// legs is fine if its sibling passes supplied the rest).</summary>
	internal static TicketParse Merge(IReadOnlyList<TicketParse> parses)
	{
		var problems = new List<string>();
		// Header qty by vote first — it doubles as the tiebreaker for per-leg qty conflicts below. A header
		// candidate that matches NO leg quantity in ANY pass is an OCR artifact (the header qty is by
		// construction one of the leg quantities), so it is dropped before voting rather than allowed to
		// deadlock the vote.
		var allLegQtys = parses.SelectMany(p => p.Legs).Select(l => l.Qty).ToHashSet();
		var hqVote = parses.Where(p => p.HeaderQty.HasValue && allLegQtys.Contains(p.HeaderQty!.Value)).GroupBy(p => p.HeaderQty!.Value).OrderByDescending(v => v.Count()).ToList();
		int? headerQty = hqVote.Count > 0 && (hqVote.Count == 1 || hqVote[0].Count() > hqVote[1].Count()) ? hqVote[0].Key : null;
		// Per-leg quantity by MAJORITY VOTE across passes: PSM 11 recovers colored-row words that PSM 4
		// drops, but fragments digits ("499" can read as "19") — two clean PSM 4 passes outvote it. On a
		// tie, the ticket's own redundant header quantity decides (that's what the field is FOR); a tie
		// the header can't break stays a Problem — we never guess on order size.
		// Canonicalize each leg's strike to the header token with the same digit sequence: passes that drop
		// the decimal ("21.5" -> "215") would otherwise union as PHANTOM legs beside the real ones. The header
		// strike field is the reference — it has survived with decimals intact in every observed snip. A digit
		// sequence that two different voted tokens claim (OCR shifting a decimal point) is dropped, not guessed.
		var (hdrTokens, hdrReadings) = HeaderStrikeReadings(parses);
		var canonTokens = hdrTokens.Where(t => t.Contains('.')).GroupBy(Digits).Where(g => g.Distinct().Count() == 1).ToDictionary(g => g.Key, g => decimal.Parse(g.First(), NumberStyles.Number, CultureInfo.InvariantCulture));
		var canonLegs = parses.SelectMany(p => p.Legs).Select(l =>
		{
			var digits = Digits(l.Strike.ToString(CultureInfo.InvariantCulture));
			return canonTokens.TryGetValue(digits, out var canon) && canon != l.Strike ? l with { Strike = canon } : l;
		});
		var byKey = canonLegs.GroupBy(l => $"{l.Action}:{l.OccSymbol}", StringComparer.Ordinal);
		var merged = new List<TicketLeg>();
		foreach (var g in byKey)
		{
			var vote = g.GroupBy(l => l.Qty).OrderByDescending(v => v.Count()).ToList();
			var qty = vote[0].Key;
			if (vote.Count > 1 && vote[0].Count() == vote[1].Count())
			{
				var tied = vote.Where(v => v.Count() == vote[0].Count()).Select(v => v.Key).ToList();
				if (headerQty.HasValue && tied.Contains(headerQty.Value)) qty = headerQty.Value;
				else problems.Add($"passes disagree on qty for {g.Key}: {string.Join(" vs ", tied)}");
			}
			merged.Add(g.First() with { Qty = qty });
		}
		merged = merged.OrderBy(l => l.OccSymbol, StringComparer.Ordinal).ToList();
		if (headerQty.HasValue)
			foreach (var l in merged.Where(l => l.Qty != headerQty.Value))
				problems.Add($"header qty {headerQty.Value} != leg qty {l.Qty} ({l.OccSymbol})");
		// Net limit by MAJORITY VOTE, not first-found: a pass that reads "$0.28" as "$0.2 8" parses 0.20, and
		// taking whichever pass sorted first would put a wrong PRICE on the place line with no cross-check to
		// catch it (the ticket repeats strikes and quantity, never the limit). A tie is a Problem.
		var limitVote = parses.Where(p => p.NetLimit.HasValue).GroupBy(p => p.NetLimit!.Value).OrderByDescending(v => v.Count()).ToList();
		var limit = limitVote.Count > 0 ? limitVote[0].Key : (decimal?)null;
		if (limitVote.Count > 1 && limitVote[0].Count() == limitVote[1].Count())
			problems.Add($"passes disagree on the net limit price: {string.Join(" vs ", limitVote.Where(v => v.Count() == limitVote[0].Count()).Select(v => v.Key.ToString("0.00", CultureInfo.InvariantCulture)))}");
		var tif = parses.Select(p => p.Tif).FirstOrDefault(t => t == "gtc") ?? "day";
		// Re-run the merged parse's own consistency checks by re-parsing the best row set is overkill; instead
		// keep every per-pass problem that is NOT a strike-arity/mismatch complaint (those are per-pass views),
		// then re-check arity once against the merged legs using the VOTED header strike field.
		problems.AddRange(parses.SelectMany(p => p.Problems).Where(x => !x.Contains("header strikes") && !x.Contains("OCR lost leg rows") && !x.Contains("no limit price") && !x.Contains("no header row") && !x.Contains("no leg rows recognized") && !x.Contains("header qty")).Distinct());
		var warnings = new List<string>();
		if (hdrTokens.Count > 0)
		{
			// Iterative: rebuilding one leg can make the NEXT one uniquely determined (e.g. two partial wing
			// rows — after the first wing is rebuilt, the residual strike, the 2P/2C type count, and the
			// vertical-complement side pin the second). Each iteration applies the same single-leg rules; the
			// loop stops the moment a leg is not fully determined.
			while (hdrTokens.Count > merged.Count)
			{
				var rebuilt = TryReconstructMissingLeg(parses, merged, hdrTokens, headerQty, warnings);
				if (rebuilt == null) break;
				merged.Add(rebuilt);
				merged = merged.OrderBy(l => l.OccSymbol, StringComparer.Ordinal).ToList();
			}
			var legStrikes = merged.Select(l => Digits(l.Strike.ToString(CultureInfo.InvariantCulture))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
			if (hdrTokens.Count != legStrikes.Length) problems.Add($"header lists {hdrTokens.Count} strikes ({string.Join("/", hdrTokens)}) but {merged.Count} leg(s) recovered across all OCR passes");
			// The multiset check is satisfied by ANY pass's reading of the field: each pass is an independent
			// look at the same cell, so one reading that reconciles with the merged legs IS the confirmation
			// this check exists to obtain. Demanding it from the single voted reading turned one pass's stray
			// space inside a token ("100/1 30", which the field regex truncates to "100/1") into a hard failure
			// on a ticket seven other passes read correctly.
			else if (!hdrReadings.Any(r => r.Select(Digits).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(legStrikes)))
				problems.Add($"header strikes {string.Join("/", hdrTokens)} != merged leg strikes {string.Join("/", merged.Select(l => l.Strike))}");
		}
		if (limit == null) problems.Add("no limit price found in any OCR pass");
		if (merged.Count == 0) problems.Add("no leg rows recognized in any OCR pass");
		return new TicketParse(merged, limit, tif, problems.Distinct().ToList(), parses.SelectMany(p => p.RowTexts).ToList()) { Warnings = warnings };
	}

	/// <summary>Consensus view of the header strike field across the OCR ensemble. Every pass reads the same
	/// cell, so the readings VOTE: the token count is the one most passes agree on (ties break toward the
	/// LARGER count so a lost-leg tripwire can never be voted away by passes that truncated a token), and the
	/// returned Consensus is the most-voted reading with that count. Readings holds every reading with that
	/// count, because a cross-check only needs ONE pass to confirm the legs.</summary>
	private static (List<string> Consensus, List<List<string>> Readings) HeaderStrikeReadings(IReadOnlyList<TicketParse> parses)
	{
		var readings = parses.Where(p => p.HeaderStrikeField != null).Select(p => FieldTokens(p.HeaderStrikeField!)).ToList();
		if (readings.Count == 0) return ([], []);
		var arity = readings.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
		var sized = readings.Where(r => r.Count == arity).ToList();
		var consensus = sized.GroupBy(r => string.Join("/", r), StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().First();
		return (consensus, sized);
	}

	/// <summary>Rebuilds ONE missing leg from readable, cross-consistent fragments — no field is guessed:
	/// the strike is the header's single unmatched token, type/side/qty come from a partial "Leg N" row that
	/// failed full parsing, the expiry is the ticket's single shared expiry, and the qty must equal the voted
	/// header qty. Any ambiguity (two missing strikes, multi-expiry ticket, no usable partial row, side or
	/// type absent) aborts — the arity Problem then stands and the place line stays suppressed. Success is
	/// reported as a WARNING so the user verifies the rebuilt leg against the ticket.</summary>
	private static TicketLeg? TryReconstructMissingLeg(IReadOnlyList<TicketParse> parses, List<TicketLeg> merged, List<string> hdrTokens, int? headerQty, List<string> warnings)
	{
		if (merged.Count == 0 || !headerQty.HasValue) return null;
		if (merged.Select(l => l.Expiry).Distinct().Count() != 1 || merged.Select(l => l.Symbol).Distinct().Count() != 1) return null;

		// Header tokens with no digit-sequence partner among the recovered legs = the missing strikes.
		var legDigits = merged.Select(l => Digits(l.Strike.ToString(CultureInfo.InvariantCulture))).ToList();
		var residual = new List<string>(hdrTokens);
		foreach (var d in legDigits)
		{
			var hit = residual.FindIndex(t => Digits(t) == d);
			if (hit < 0) return null;   // header and legs don't reconcile — reconstruction has no reference
			residual.RemoveAt(hit);
		}
		if (residual.Count == 0) return null;

		// A partial leg row binds to a residual strike by digit match; it must also supply the expected qty
		// and a type (readable or deducible). Its leg number must not already be fully parsed.
		var parsedLegNumbers = parses.SelectMany(p => p.RowTexts).SelectMany(t => { var m = LegRx.Match(t); return m.Success ? new[] { Regex.Match(t, @"^Leg\s*(\d+)", RegexOptions.IgnoreCase).Groups[1].Value } : []; }).ToHashSet();
		foreach (var row in parses.SelectMany(p => p.RowTexts).Distinct())
		{
			var head = Regex.Match(row, @"^Leg\s*(\d+)\b", RegexOptions.IgnoreCase);
			if (!head.Success || parsedLegNumbers.Contains(head.Groups[1].Value)) continue;
			if (!Regex.IsMatch(row, $@"(?<![\d]){headerQty.Value}(?![\d])")) continue;

			// Bind the row to a residual strike: the row's strike token (first number after the symbol) must
			// digit-match exactly one residual header token; the header token's decimal form is authoritative.
			var rowStrike = Regex.Match(row, @"^Leg\s*\d+\s+[A-Z]{1,6}\s+[^\d]{0,2}(\d+(?:\s*\.\s*\d+)?)\s", RegexOptions.IgnoreCase);
			if (!rowStrike.Success) continue;
			var rowDigits = Digits(rowStrike.Groups[1].Value);
			var token = residual.Where(t => Digits(t) == rowDigits).Distinct().ToList();
			// Fallback: a truncated strike token ("2" from "22") matches nothing anywhere — if it matches no
			// HEADER token either and exactly ONE strike is missing, the row can only be that leg.
			if (token.Count == 0 && residual.Count == 1 && !hdrTokens.Any(t => Digits(t) == rowDigits)) token = [residual[0]];
			if (token.Count != 1 || !decimal.TryParse(token[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var strike)) continue;

			var cp = Regex.Match(row, @"\b(Call|Put)\b", RegexOptions.IgnoreCase);
			string cpVal, cpProvenance;
			if (cp.Success)
			{
				cpVal = cp.Groups[1].Value[..1].ToUpperInvariant();
				cpProvenance = "type from partial row";
			}
			else if (hdrTokens.Count == 4 && merged.Any(l => l.CallPut == "P") && merged.Any(l => l.CallPut == "C") && merged.Count == 3)
			{
				// Mixed-type 4-strike ticket = iron structure = exactly 2 puts + 2 calls; with 3 legs recovered
				// the deficient type is forced.
				cpVal = merged.Count(l => l.CallPut == "P") < 2 ? "P" : "C";
				cpProvenance = "type DEDUCED as the deficient type of the 2-put/2-call iron structure";
			}
			else continue;

			var side = Regex.Match(row, @"(Buy|Sell)", RegexOptions.IgnoreCase);
			string sideVal, sideProvenance;
			if (side.Success)
			{
				sideVal = side.Groups[1].Value.ToLowerInvariant();
				sideProvenance = "side from partial row";
			}
			else
			{
				// Side unreadable (OCR gave "By"/"xy" — NEVER fuzzy-matched: one letter separates Buy from
				// Sell). One deduction is structure-FORCED, not guessed: on a 4-strike ticket where the missing
				// leg's type has exactly ONE recovered same-type leg, the ticket is an iron structure (a
				// single-type condor can never present exactly one same-type leg there) and each type forms a
				// vertical — the missing side is the recovered leg's complement; anything else would be an
				// unbounded short inside a defined-risk ticket.
				var sameType = merged.Where(l => l.CallPut == cpVal).ToList();
				if (hdrTokens.Count != 4 || sameType.Count != 1) continue;
				sideVal = sameType[0].Action == "sell" ? "buy" : "sell";
				sideProvenance = $"side DEDUCED as the vertical complement of the recovered {sameType[0].Action} {sameType[0].OccSymbol} (defined-risk structure)";
			}
			var leg = new TicketLeg(merged[0].Symbol, strike, merged[0].Expiry, cpVal, sideVal, headerQty.Value);
			if (merged.Any(l => l.OccSymbol == leg.OccSymbol && l.Action == leg.Action)) continue;   // duplicate — row bound to an already-recovered leg
			warnings.Add($"Leg reconstructed from fragments: {leg.Action} {leg.OccSymbol} x{leg.Qty} — strike from the header field, {cpProvenance}, {sideProvenance}, qty from partial row \"{row.Trim()}\". VERIFY against the ticket before placing.");
			return leg;
		}
		return null;
	}

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
		int? headerQty = null;
		string? headerStrikeField = null;
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
			// Header strike field: N slash-separated strikes, one per leg ("21.5/21.5", "20.5/21.5/21.5/23").
			// Two checks, both against DIGIT SEQUENCES rather than parsed values — tesseract's known failure
			// mode on the cramped header cell is dropping the decimal point ("21.5" reads as "215"), and the
			// digits-only comparison forgives exactly that while still catching real mismatches. The leg rows
			// remain the authoritative strike values.
			//   1. ARITY: the slash-count says how many legs the ticket REALLY has. Fewer parsed legs means
			//      OCR lost whole rows — without this check a 4-leg condor whose legs 3/4 vanish would emit a
			//      2-leg order at the full-structure limit price.
			//   2. MULTISET: when the counts match, every header strike must pair up with a leg strike.
			var hs = HeaderStrikeFieldRx.Match(header);
			if (hs.Success)
			{
				headerStrikeField = string.Join("/", FieldTokens(hs.Value));
				var hdrStrikes = FieldTokens(hs.Value).Select(Digits).OrderBy(x => x, StringComparer.Ordinal).ToArray();
				if (hdrStrikes.Length != legs.Count)
				{
					problems.Add($"header lists {hdrStrikes.Length} strikes ({headerStrikeField}) but only {legs.Count} leg row(s) parsed — OCR lost leg rows");
				}
				else
				{
					var legStrikes = legs.Select(l => Digits(l.Strike.ToString(CultureInfo.InvariantCulture))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
					if (!hdrStrikes.SequenceEqual(legStrikes)) problems.Add($"header strikes {headerStrikeField} != leg strikes {string.Join("/", legs.Select(l => l.Strike))}");
				}
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
				headerQty = int.Parse(Regex.Replace(hq.Groups[1].Value, "[ ,]", ""), CultureInfo.InvariantCulture);
				foreach (var l in legs.Where(l => l.Qty != headerQty)) problems.Add($"header qty {headerQty} != leg qty {l.Qty} ({l.OccSymbol})");
			}
		}

		if (legs.Count == 0) problems.Add("no leg rows recognized");
		if (limit == null) problems.Add("no limit price found in header");
		if (legs.Select(l => l.Symbol).Distinct().Count() > 1) problems.Add("legs disagree on the underlying symbol");

		return new TicketParse(legs, limit, tif, problems, rowTexts, headerQty) { HeaderStrikeField = headerStrikeField };
	}
}
