using WebullAnalytics;

namespace WebullAnalytics.Tests;

/// <summary>Compact constructor for OptionContractQuote in tests. Only sets the fields tests need.</summary>
internal static class TestQuote
{
    public static OptionContractQuote Q(decimal bid, decimal ask, decimal? iv = null) =>
        new OptionContractQuote(
            ContractSymbol: "",
            LastPrice: null,
            Bid: bid,
            Ask: ask,
            Change: null,
            PercentChange: null,
            Volume: null,
            OpenInterest: null,
            ImpliedVolatility: iv);
}
