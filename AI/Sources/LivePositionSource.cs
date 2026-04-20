namespace WebullAnalytics.AI.Sources;

/// <summary>
/// PHASE-1 STUB. Returns empty positions and zeroed account state so the pipeline is runnable
/// end-to-end. Real OpenAPI wiring is deferred to a follow-up task that will:
///   1. Add Webull OpenAPI positions + balance endpoints to WebullOpenApiClient.
///   2. Port strategy-grouping logic (from StrategyGrouper.cs) to map broker legs into OpenPosition records.
/// Until then, `ai once` and `ai watch` will report "0 position(s)" even when positions exist
/// in the account — this is intentional, not a bug.
/// </summary>
internal sealed class LivePositionSource : IPositionSource
{
	private readonly TradeAccount _account;

	public LivePositionSource(TradeAccount account) { _account = account; }

	public Task<IReadOnlyDictionary<string, OpenPosition>> GetOpenPositionsAsync(
		DateTime asOf, IReadOnlySet<string> tickers, CancellationToken cancellation) =>
		Task.FromResult<IReadOnlyDictionary<string, OpenPosition>>(new Dictionary<string, OpenPosition>());

	public Task<(decimal cash, decimal accountValue)> GetAccountStateAsync(DateTime asOf, CancellationToken cancellation) =>
		Task.FromResult((0m, 0m));
}
