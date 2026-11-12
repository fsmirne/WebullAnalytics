using System.Collections.Concurrent;

namespace WebullAnalytics.Analyze;

/// <summary>Memo of the cascade replay's session-level loads — the inputs that depend on the session and expiry, never on the checkpoint:
/// the prior day's causal chain, the prior day's ΔOI basis, the front expiry's minute-NBBO slice, and the store's volume tape. The panel
/// evaluates one instant and passes none (each load runs once anyway); `analyze cascade-study` evaluates a dozen checkpoints per session
/// and passes one per session, so each load runs once per session instead of once per checkpoint. The same-window volume baseline reads
/// up to twenty prior sessions per checkpoint; its per-day minute totals are immutable store data, so that one map is shared across
/// sessions (<paramref name="sharedMinuteVolume"/>) rather than dropped with each session's memo.</summary>
internal sealed class CascadeReplayCache(ConcurrentDictionary<(string Ticker, DateTime Date, DateTime Expiry), Lazy<SortedDictionary<TimeSpan, long>?>> sharedMinuteVolume)
{
	private readonly ConcurrentDictionary<(string, DateTime), Lazy<(DateTime? PriorDate, Dictionary<string, OptionContractQuote> Quotes)>> _priorChain = new();
	private readonly ConcurrentDictionary<(string, DateTime), Lazy<(DateTime? Date, Dictionary<(DateTime Expiry, decimal Strike, bool IsCall), PriorContract>? Oi)>> _priorOi = new();
	private readonly ConcurrentDictionary<(string, DateTime, DateTime), Lazy<AnalyzeGexCommand.IntradayQuoteSlice?>> _slices = new();
	private readonly ConcurrentDictionary<(string, DateTime, DateTime), Lazy<SortedDictionary<TimeSpan, Dictionary<decimal, (decimal C, decimal P)>>>> _tapes = new();

	internal (DateTime? PriorDate, Dictionary<string, OptionContractQuote> Quotes) PriorChain(string ticker, DateTime date, Func<(DateTime?, Dictionary<string, OptionContractQuote>)> load) =>
		Memo(_priorChain, (ticker.ToUpperInvariant(), date.Date), load);

	internal (DateTime? Date, Dictionary<(DateTime Expiry, decimal Strike, bool IsCall), PriorContract>? Oi) PriorOi(string ticker, DateTime date) =>
		Memo(_priorOi, (ticker.ToUpperInvariant(), date.Date), () => AnalyzeGexCommand.LoadPriorOi(ticker, date));

	internal AnalyzeGexCommand.IntradayQuoteSlice? QuoteSlice(string ticker, DateTime date, DateTime expiry, Func<AnalyzeGexCommand.IntradayQuoteSlice?> open) =>
		Memo(_slices, (ticker.ToUpperInvariant(), date.Date, expiry.Date), open);

	internal SortedDictionary<TimeSpan, Dictionary<decimal, (decimal C, decimal P)>> StoreTape(string ticker, DateTime date, DateTime expiry) =>
		Memo(_tapes, (ticker.ToUpperInvariant(), date.Date, expiry.Date), () => AnalyzeGexCommand.LoadStoreTape(ticker, date, expiry));

	internal SortedDictionary<TimeSpan, long>? MinuteVolume(string ticker, DateTime date, DateTime expiry) =>
		Memo(sharedMinuteVolume, (ticker.ToUpperInvariant(), date.Date, expiry.Date), () => AnalyzeGexCommand.StoreMinuteVolume(ticker, date, expiry));

	private static T Memo<TKey, T>(ConcurrentDictionary<TKey, Lazy<T>> map, TKey key, Func<T> load) where TKey : notnull =>
		map.GetOrAdd(key, _ => new Lazy<T>(load)).Value;
}
