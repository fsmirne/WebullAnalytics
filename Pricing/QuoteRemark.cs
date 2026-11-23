namespace WebullAnalytics.Pricing;

/// <summary>Re-marks an option board struck at one underlying spot to another, holding each contract's own implied vol
/// (sticky strike). For a book whose quotes are stuck at a prior print — the 16:00/16:15 close overnight, or any live
/// book under a hypothetical --spot — this is the board the evaluation spot implies: every consumer (order limits,
/// close-now, conversion credits, the opener scorer, the arb edge) then reads mids consistent with the spot it is
/// evaluated at, instead of mixing stale mids with a moved spot.</summary>
internal static class QuoteRemark
{
	/// <summary>Returns <paramref name="quotes"/> with every <paramref name="root"/> quote (or its SPX/SPXW sibling) moved from
	/// <paramref name="fromSpot"/> to <paramref name="toSpot"/>. Each contract's vol is solved from its two-sided mid at
	/// <paramref name="fromSpot"/> (else its existing IV) on the same clock as the live IV back-solve, and its bid, ask and last
	/// shift by the model's price change at that vol; the spread width is kept. The returned IV is that sticky vol, so a later
	/// back-solve at <paramref name="toSpot"/> recovers it. A quote with no vol to hold, or past expiry, passes through
	/// unchanged — <paramref name="unpriced"/> counts the ones that had a price to move.</summary>
	internal static IReadOnlyDictionary<string, OptionContractQuote> StickyStrike(IReadOnlyDictionary<string, OptionContractQuote> quotes, string root, decimal fromSpot, decimal toSpot, out int unpriced)
	{
		unpriced = 0;
		var roots = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { [root] = fromSpot };
		var result = new Dictionary<string, OptionContractQuote>(quotes.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var (sym, q) in quotes)
		{
			var p = ParsingHelpers.ParseOptionSymbol(sym);
			if (p is null || p.CallPut is not ("C" or "P") || !ParsingHelpers.TryResolveForRoot(roots, p.Root, out _)) { result[sym] = q; continue; }
			var remarked = Remark(q, p, fromSpot, toSpot);
			if (remarked is null && (q.Bid is > 0m || q.Ask is > 0m || q.LastPrice is > 0m)) unpriced++;
			result[sym] = remarked ?? q;
		}
		return result;
	}

	private static OptionContractQuote? Remark(OptionContractQuote q, OptionParsed p, decimal fromSpot, decimal toSpot)
	{
		var t = MarketClock.Between(p.Root, OptionMath.ObservationInstant(), p.ExpiryDate.Date + OptionMath.MarketClose);
		if (t.IsExpired) return null;

		var twoSided = q.Bid is > 0m && q.Ask is > 0m && q.Ask.Value >= q.Bid.Value;
		var mid = twoSided ? (q.Bid!.Value + q.Ask!.Value) / 2m : 0m;
		// Same guard as the live back-solve: a deep-ITM bid under intrinsic makes the solved vol noise.
		var sigma = twoSided && q.Bid!.Value > OptionMath.Intrinsic(fromSpot, p.Strike, p.CallPut)
			? OptionMath.ImpliedVol(fromSpot, p.Strike, t, OptionMath.RiskFreeRate, mid, p.CallPut)
			: q.ImpliedVolatility;
		if (sigma is not > 0m) return null;

		var shift = OptionMath.BlackScholes(toSpot, p.Strike, t, OptionMath.RiskFreeRate, sigma.Value, p.CallPut)
				  - OptionMath.BlackScholes(fromSpot, p.Strike, t, OptionMath.RiskFreeRate, sigma.Value, p.CallPut);

		decimal? bid, ask;
		if (twoSided)
		{
			var halfSpread = (q.Ask!.Value - q.Bid!.Value) / 2m;
			var newMid = Math.Max(0m, mid + shift);
			bid = Positive(newMid - halfSpread);
			ask = Positive(newMid + halfSpread);
		}
		else
		{
			bid = q.Bid is > 0m ? Positive(q.Bid.Value + shift) : q.Bid;
			ask = q.Ask is > 0m ? Positive(q.Ask.Value + shift) : q.Ask;
		}
		var last = q.LastPrice is > 0m ? Positive(q.LastPrice.Value + shift) : q.LastPrice;
		return q with { Bid = bid, Ask = ask, LastPrice = last, ImpliedVolatility = sigma, VendorImpliedVolatility = q.VendorImpliedVolatility ?? q.ImpliedVolatility };
	}

	private static decimal? Positive(decimal price) => price > 0m ? Math.Round(price, 4) : null;
}
