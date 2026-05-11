namespace WebullAnalytics.AI.Events;

/// <summary>Immutable per-tick map of ticker → <see cref="TickerEvents"/>. Built once by the opener
/// evaluator and passed through to scoring. Tickers absent from the map are treated as "no events
/// known" — the veto degrades to false, the diagnostic rule stays quiet.</summary>
internal sealed class EventCalendar
{
	private readonly IReadOnlyDictionary<string, TickerEvents> _byTicker;

	public static EventCalendar Empty { get; } = new(new Dictionary<string, TickerEvents>(StringComparer.OrdinalIgnoreCase));

	public EventCalendar(IReadOnlyDictionary<string, TickerEvents> byTicker)
	{
		_byTicker = byTicker;
	}

	public TickerEvents? Get(string ticker)
	{
		if (string.IsNullOrWhiteSpace(ticker)) return null;
		return _byTicker.TryGetValue(ticker, out var ev) ? ev : null;
	}

	public int Count => _byTicker.Count;
}
