namespace WebullAnalytics;

/// <summary>One option leg as margin sees it: the contract, how many, which side, and (for a short) the premium that sits in the
/// naked-short formula.</summary>
internal readonly record struct MarginLeg(OptionParsed Option, int Qty, bool IsLong, decimal PremiumPerShare = 0m);

/// <summary>A margin requirement in dollars for the whole set of legs, and how it was reached.</summary>
internal sealed record MarginRequirement(decimal Total, string Label);

/// <summary>
/// The one place broker margin is computed (Webull's documented rules, Reg-T where Webull is silent). Margin is the COLLATERAL a position ties up
/// beyond what it already paid: a long option is paid in full and needs none; a debit spread or a covered calendar/diagonal (the long
/// leg expires with or after the short and finishes at least as valuable) needs none; a spread whose long can finish worth less than
/// the short — a credit vertical, an inverted diagonal — holds that strike loss (the most the pair can owe at the short's expiry);
/// an uncovered short holds the naked-short formula (<see cref="NakedShortPerContract"/>). The debit paid for a position is cash spent, not margin: an inverted put diagonal
/// 760/759 x100 holds $1 x 100 x 100 = $10,000, not $10,000 plus its debit. A short call can also be covered by 100 shares of stock.
/// An iron condor or butterfly (every short covered, one expiry, a strike loss on both sides) holds only the larger side, because
/// both shorts cannot finish in the money at once. Different-expiry structures with a loss on both sides hold the sum.
/// </summary>
internal static class MarginCalculator
{
	/// <summary>Naked short per contract (100 shares), the highest of Webull's documented requirements (help FAQ 657, Level 4): the
	/// premium plus 20% of the underlying less the out-of-the-money amount (Reg-T's base; Webull's per-ticker "Margin Requirement %"
	/// is undocumented, and 20% is the Cboe rate for equity and ETF options such as SPY); for a call, the premium plus 10% of the
	/// underlying (Reg-T's minimum — Webull's own call line is garbled on its page); for a put, the premium plus 15% of the strike
	/// (Webull, stricter than Reg-T's 10%); and $250.</summary>
	internal static decimal NakedShortPerContract(decimal spot, decimal strike, string callPut, decimal premiumPerShare)
	{
		var isCall = callPut == "C";
		var otm = isCall ? Math.Max(strike - spot, 0m) : Math.Max(spot - strike, 0m);
		var basePerShare = premiumPerShare + NakedBasePct * spot - otm;
		var minimumPerShare = premiumPerShare + (isCall ? CallMinimumPct * spot : PutMinimumPct * strike);
		return Math.Max(Math.Max(basePerShare, minimumPerShare) * 100m, NakedFloorPerContract);
	}

	internal const decimal NakedBasePct = 0.20m;
	internal const decimal CallMinimumPct = 0.10m;
	internal const decimal PutMinimumPct = 0.15m;
	internal const decimal NakedFloorPerContract = 250m;

	/// <summary>Strike loss per share of a short covered by a long of the same type: what the pair can owe at the short's expiry
	/// (0 when the long finishes at least as valuable).</summary>
	internal static decimal StrikeLossPerShare(OptionParsed shortLeg, OptionParsed longLeg) =>
		shortLeg.CallPut == "C" ? Math.Max(longLeg.Strike - shortLeg.Strike, 0m) : Math.Max(shortLeg.Strike - longLeg.Strike, 0m);

	/// <summary>A long covers a short when it is the same underlying (SPX and SPXW are both the S&P 500 index) and type and expires
	/// with or after it.</summary>
	internal static bool Covers(OptionParsed longLeg, OptionParsed shortLeg) =>
		longLeg.CallPut == shortLeg.CallPut && longLeg.ExpiryDate.Date >= shortLeg.ExpiryDate.Date
		&& (string.Equals(longLeg.Root, shortLeg.Root, StringComparison.OrdinalIgnoreCase) || ParsingHelpers.IsIndexMonthlyFragmentedRoot(longLeg.Root) && ParsingHelpers.IsIndexMonthlyFragmentedRoot(shortLeg.Root));

	/// <summary>The requirement for <paramref name="legs"/> at <paramref name="spot"/>, with <paramref name="longShares"/> shares of the
	/// underlying available to cover short calls. Each short (earliest expiry first) takes the covering long with the smallest strike
	/// loss; whatever no long or stock covers is naked.</summary>
	internal static MarginRequirement Requirement(IReadOnlyList<MarginLeg> legs, decimal spot, int longShares = 0)
	{
		var pool = legs.Where(l => l.IsLong && l.Qty > 0).Select(l => new CoverSlot(l.Option, l.Qty)).ToList();
		var sharesLeft = Math.Max(0, longShares);
		decimal calls = 0m, puts = 0m;
		var naked = false;
		var parts = new List<string>();
		foreach (var s in legs.Where(l => !l.IsLong && l.Qty > 0).OrderBy(l => l.Option.ExpiryDate))
		{
			var remaining = s.Qty;
			while (remaining > 0)
			{
				var slot = pool.Where(p => p.Qty > 0 && Covers(p.Option, s.Option)).OrderBy(p => StrikeLossPerShare(s.Option, p.Option)).ThenBy(p => p.Option.ExpiryDate).FirstOrDefault();
				if (slot == null) break;
				var n = Math.Min(remaining, slot.Qty);
				slot.Qty -= n;
				remaining -= n;
				var perContract = StrikeLossPerShare(s.Option, slot.Option) * 100m;
				AddSide(s.Option, perContract * n, ref calls, ref puts);
				parts.Add($"{PairName(s.Option, slot.Option)} ${perContract:N2}/contract × {n}");
			}
			if (remaining > 0 && s.Option.CallPut == "C" && sharesLeft >= 100)
			{
				var n = Math.Min(remaining, sharesLeft / 100);
				sharesLeft -= n * 100;
				remaining -= n;
				parts.Add($"covered by stock $0.00/contract × {n}");
			}
			if (remaining > 0)
			{
				var perContract = NakedShortPerContract(spot, s.Option.Strike, s.Option.CallPut, s.PremiumPerShare);
				AddSide(s.Option, perContract * remaining, ref calls, ref puts);
				naked = true;
				parts.Add($"naked ${perContract:N2}/contract × {remaining}");
			}
		}
		var oneExpiry = legs.Select(l => l.Option.ExpiryDate.Date).Distinct().Count() == 1;
		var greaterSide = !naked && oneExpiry && calls > 0m && puts > 0m;
		var total = greaterSide ? Math.Max(calls, puts) : calls + puts;
		var label = parts.Count == 0 ? "no short legs $0.00" : string.Join("; ", parts) + (greaterSide ? " (iron spread: greater side only)" : "");
		return new MarginRequirement(total, label);
	}

	/// <summary>Per-contract requirement of one short paired with an optional long (one contract each) — the shape every roll and
	/// position scenario prices.</summary>
	internal static decimal PairPerContract(OptionParsed shortLeg, decimal shortPremiumPerShare, OptionParsed? longLeg, decimal spot)
	{
		var legs = new List<MarginLeg> { new(shortLeg, 1, IsLong: false, shortPremiumPerShare) };
		if (longLeg != null) legs.Add(new(longLeg, 1, IsLong: true));
		return Requirement(legs, spot).Total;
	}

	private static void AddSide(OptionParsed shortLeg, decimal amount, ref decimal calls, ref decimal puts)
	{
		if (shortLeg.CallPut == "C") calls += amount;
		else puts += amount;
	}

	private static string PairName(OptionParsed shortLeg, OptionParsed longLeg)
	{
		var sameExpiry = longLeg.ExpiryDate.Date == shortLeg.ExpiryDate.Date;
		if (StrikeLossPerShare(shortLeg, longLeg) == 0m)
			return longLeg.Strike == shortLeg.Strike ? (sameExpiry ? "offsetting" : "calendar") : sameExpiry ? "debit vertical" : "covered diagonal";
		return sameExpiry ? "credit vertical" : "inverted diagonal";
	}

	private sealed class CoverSlot(OptionParsed option, int qty)
	{
		internal OptionParsed Option { get; } = option;
		internal int Qty { get; set; } = qty;
	}
}
