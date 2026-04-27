namespace WebullAnalytics.AI.Output;

internal static class SuggestionPricing
{
	internal const string Mid = "mid";
	internal const string BidAsk = "bidask";

	internal static string Normalize(string? mode) => string.Equals(mode, BidAsk, StringComparison.OrdinalIgnoreCase)
		? BidAsk
		: Mid;

	internal static decimal? PriceFor(ProposalLeg leg, string mode) => Normalize(mode) == BidAsk
		? leg.ExecutionPricePerShare ?? leg.PricePerShare
		: leg.PricePerShare ?? leg.ExecutionPricePerShare;

	internal static decimal? TryGetLimitPerShare(IEnumerable<ProposalLeg> legs, string mode)
	{
		decimal signedNet = 0m;
		foreach (var leg in legs)
		{
			var price = PriceFor(leg, mode);
			if (!price.HasValue) return null;
			signedNet += leg.Action == "sell" ? price.Value : -price.Value;
		}

		return Math.Abs(signedNet);
	}

	internal static string AnalyzeKeywordFor(ProposalLeg leg, string mode) => Normalize(mode) == BidAsk
		? leg.Action == "buy" ? "ASK" : "BID"
		: "MID";
}
