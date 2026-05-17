namespace WebullAnalytics.AI.Backtest;

/// <summary>One realized fill in the backtest ledger. Each managed position generates an Open fill, possibly
/// one or more Roll fills (each emits a Close + Open pair), and one Close fill (or Expire).</summary>
internal sealed record BacktestFill(
	DateTime Date,
	string PositionKey,
	string Ticker,
	string StrategyKind,   // e.g. LongCalendar, IronCondor
	int Qty,               // contract count for the whole order
	BacktestFillKind Kind,
	long LineageId,        // monotonic id assigned on Open; propagated through Roll; terminal on Close/Expire
	IReadOnlyList<BacktestLegFill> Legs,
	decimal NetCashFlow,   // signed: +credit, -debit. Excludes fees.
	decimal Fees,
	string? RuleName       // null for Open, else the management rule that fired
);

internal sealed record BacktestLegFill(
	string Symbol,
	Side Side,           // Buy or Sell (the executed action, not the original leg direction)
	int Qty,
	decimal PricePerShare
);

internal enum BacktestFillKind { Open, Close, Roll, Expire }

/// <summary>
/// In-memory simulated book + cash ledger driven by the backtest runner. The runner re-prices
/// each fill leg from the current quote source and passes pre-computed total-dollar cash flows
/// (POSITIVE = credit received, NEGATIVE = debit paid). Fees are applied per-leg-contract:
/// <c>totalFees = qty * legs.Count * feePerContract</c>.
/// </summary>
internal sealed class SimulatedBook
{
	private const decimal Multiplier = 100m;

	// Webull's per-contract commission schedule: cash-settled index options (SPX/SPXW/NDX/XSP/RUT)
	// carry a $1.14/contract fee that bundles the index-options surcharge with ORF + clearing;
	// equity and ETF options are effectively $0.05 (regulatory fees only after the $0 base
	// commission). Default the backtest fee from the ticker so a user running
	// `wa ai backtest SPXW` doesn't silently get equity pricing.
	public const decimal IndexOptionFeePerContract = 1.14m;
	public const decimal EquityOptionFeePerContract = 0.05m;

	private static readonly HashSet<string> IndexOptionTickers = new(StringComparer.OrdinalIgnoreCase)
	{
		"SPX", "SPXW", "NDX", "XSP", "RUT", "DJX", "VIX"
	};

	/// <summary>Per-leg-contract commission default for <paramref name="ticker"/>. Cash-settled index
	/// options charge the higher $1.14/contract; equity/ETF options charge the lower $0.05/contract.</summary>
	public static decimal DefaultFeePerContractFor(string ticker) =>
		IndexOptionTickers.Contains(ticker) ? IndexOptionFeePerContract : EquityOptionFeePerContract;

	private readonly Dictionary<string, OpenPosition> _positions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, decimal> _initialDebitPerContract = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, decimal> _adjustedDebitPerContract = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _lineageByKey = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<BacktestFill> _fills = new();
	private long _nextLineageId = 1;

	private readonly decimal _feePerContract;
	private readonly OpenerRealizedExpectancyConfig _slippageConfig;

	public SimulatedBook(decimal startingCash, decimal feePerContract, OpenerRealizedExpectancyConfig slippageConfig)
	{
		Cash = startingCash;
		_feePerContract = feePerContract;
		_slippageConfig = slippageConfig;
	}

	/// <summary>Per-order slippage cost in dollars for the given structure × qty. Deducted from each
	/// fill's NetCashFlow so the realized cash flow matches the friction-aware EV the scorer used.</summary>
	private decimal ComputeSlippage(OpenStructureKind structureKind, int qty) =>
		RealizedExpectancy.ComputeFrictionPerOrderPerContract(_slippageConfig, structureKind) * Math.Abs(qty);

	public decimal Cash { get; private set; }
	public IReadOnlyDictionary<string, OpenPosition> OpenPositions => _positions;
	public IReadOnlyList<BacktestFill> Fills => _fills;

	/// <summary>Realized P&amp;L = cash flow on closed/expired positions only. Open positions' mark-to-market is excluded.</summary>
	public decimal RealizedPnL => _fills.Sum(f => f.NetCashFlow - f.Fees);

	public decimal TotalFees => _fills.Sum(f => f.Fees);

	public int OpenCount => _positions.Count;

	/// <param name="legFills">Per-leg fills. <c>PricePerShare</c> must be set; <c>Side</c> is the executed direction.</param>
	public bool Open(DateTime date, string ticker, OpenStructureKind structureKind, IReadOnlyList<BacktestLegFill> legFills, int qty)
	{
		var positionLegs = BuildLegsFromFills(legFills, qty, out var strategyKind, structureKind);
		if (positionLegs.Count == 0) return false;

		var key = ComputeKey(ticker, strategyKind, positionLegs);
		if (_positions.ContainsKey(key)) return false;

		// Deduct slippage from cash flow so realized P&L matches the friction-aware EV the scorer used.
		var cashFlow = ComputeCashFlow(legFills) - ComputeSlippage(structureKind, qty);
		var fees = Math.Abs(qty) * legFills.Count * _feePerContract;
		Cash += cashFlow - fees;

		// Track InitialNetDebit / AdjustedNetDebit in PER-SHARE dollars to match the broker / live-source
		// convention (PositionRow.AvgPrice). Rule evaluators (StopLoss, TakeProfit) compute mark per-share
		// from quote mids and subtract these debits; storing per-contract here would create a 100× unit
		// mismatch and cause TakeProfitRule to fire on big losses. Sign: positive = debit paid, negative
		// = credit received.
		var perSharePnL = qty != 0 ? cashFlow / (qty * Multiplier) : 0m;
		var initialDebitPerShare = -perSharePnL;
		_positions[key] = new OpenPosition(
			Key: key,
			Ticker: ticker,
			StrategyKind: strategyKind,
			Legs: positionLegs,
			InitialNetDebit: initialDebitPerShare,
			AdjustedNetDebit: initialDebitPerShare,
			Quantity: qty,
			OpenedAt: date,
			MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebitPerShare, positionLegs));
		_initialDebitPerContract[key] = initialDebitPerShare;
		_adjustedDebitPerContract[key] = initialDebitPerShare;
		var lineageId = _nextLineageId++;
		_lineageByKey[key] = lineageId;

		_fills.Add(new BacktestFill(date, key, ticker, strategyKind, qty, BacktestFillKind.Open, lineageId, legFills, cashFlow, fees, null));
		return true;
	}

	public bool Close(DateTime date, string positionKey, IReadOnlyList<BacktestLegFill> legFills, string ruleName)
	{
		if (!_positions.TryGetValue(positionKey, out var pos)) return false;

		var structureKind = Enum.Parse<OpenStructureKind>(pos.StrategyKind, ignoreCase: true);
		var cashFlow = ComputeCashFlow(legFills) - ComputeSlippage(structureKind, pos.Quantity);
		var fees = Math.Abs(pos.Quantity) * legFills.Count * _feePerContract;
		Cash += cashFlow - fees;

		var lineageId = _lineageByKey[positionKey];
		_fills.Add(new BacktestFill(date, positionKey, pos.Ticker, pos.StrategyKind, pos.Quantity, BacktestFillKind.Close, lineageId, legFills, cashFlow, fees, ruleName));

		_positions.Remove(positionKey);
		_initialDebitPerContract.Remove(positionKey);
		_adjustedDebitPerContract.Remove(positionKey);
		_lineageByKey.Remove(positionKey);
		return true;
	}

	public bool Roll(DateTime date, string oldPositionKey, IReadOnlyList<BacktestLegFill> legFills, string ruleName, OpenStructureKind? newStructureKind = null)
	{
		if (!_positions.TryGetValue(oldPositionKey, out var oldPos)) return false;

		// A roll is one combo execution = one slippage cross. The destination structure determines the
		// order count (single vs double-side structures), matching the scorer's friction assumption.
		var slipStructure = newStructureKind ?? Enum.Parse<OpenStructureKind>(oldPos.StrategyKind, ignoreCase: true);
		var cashFlow = ComputeCashFlow(legFills) - ComputeSlippage(slipStructure, oldPos.Quantity);
		var fees = Math.Abs(oldPos.Quantity) * legFills.Count * _feePerContract;
		Cash += cashFlow - fees;

		// Synthesize the post-roll leg set: keep buys, replace short(s) with the new short(s) the rule chose.
		// In this engine a Roll proposal expresses the *delta* (closing legs + opening legs as a single net debit).
		// We rebuild PositionLegs from the resulting structure by reusing rollLegs filtered to the new
		// short/long sides. For phase-1 we treat the rolled position's leg set as the rollLegs themselves —
		// upstream rules emit a complete new structure.
		var structureKind = newStructureKind ?? Enum.Parse<OpenStructureKind>(oldPos.StrategyKind, ignoreCase: true);
		var newLegs = BuildLegsFromFills(legFills, oldPos.Quantity, out var strategyKind, structureKind);
		if (newLegs.Count == 0)
		{
			newLegs = oldPos.Legs;
			strategyKind = oldPos.StrategyKind;
		}

		var newKey = ComputeKey(oldPos.Ticker, strategyKind, newLegs);
		var initialDebit = _initialDebitPerContract[oldPositionKey];
		// Roll adjusts the per-share basis by the per-share cash flow of the roll. Same sign convention
		// as Open: positive = additional debit paid, negative = additional credit received.
		var perShareDelta = oldPos.Quantity != 0 ? -cashFlow / (oldPos.Quantity * Multiplier) : 0m;
		var adjustedDebit = _adjustedDebitPerContract[oldPositionKey] + perShareDelta;

		var lineageId = _lineageByKey[oldPositionKey];
		_positions.Remove(oldPositionKey);
		_initialDebitPerContract.Remove(oldPositionKey);
		_adjustedDebitPerContract.Remove(oldPositionKey);
		_lineageByKey.Remove(oldPositionKey);

		_positions[newKey] = new OpenPosition(
			Key: newKey,
			Ticker: oldPos.Ticker,
			StrategyKind: strategyKind,
			Legs: newLegs,
			InitialNetDebit: initialDebit,
			AdjustedNetDebit: adjustedDebit,
			Quantity: oldPos.Quantity,
			OpenedAt: oldPos.OpenedAt,
			MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebit, newLegs));
		_initialDebitPerContract[newKey] = initialDebit;
		_adjustedDebitPerContract[newKey] = adjustedDebit;
		_lineageByKey[newKey] = lineageId;

		_fills.Add(new BacktestFill(date, newKey, oldPos.Ticker, strategyKind, oldPos.Quantity, BacktestFillKind.Roll, lineageId, legFills, cashFlow, fees, ruleName));
		return true;
	}

	/// <summary>Settle a position whose short leg has expired. Fills both legs at intrinsic value: short pays out
	/// max(0, intrinsic), long collects max(0, intrinsic) at its own strike (only meaningful for diagonals where
	/// the long is still alive — but for symmetry we settle the whole structure at the expiry-day spot).</summary>
	public bool Expire(DateTime date, string positionKey, decimal spotAtExpiry)
	{
		if (!_positions.TryGetValue(positionKey, out var pos)) return false;

		var settlementPerContract = 0m;
		var settlementLegs = new List<BacktestLegFill>();
		foreach (var leg in pos.Legs)
		{
			if (leg.Expiry == null || leg.CallPut == null) continue;
			var intrinsic = leg.CallPut == "C" ? Math.Max(0m, spotAtExpiry - leg.Strike) : Math.Max(0m, leg.Strike - spotAtExpiry);
			// Long legs collect intrinsic on the way out; short legs pay it.
			if (leg.Side == Side.Buy)
			{
				settlementPerContract += intrinsic;
				settlementLegs.Add(new BacktestLegFill(leg.Symbol, Side.Sell, leg.Qty, intrinsic));
			}
			else
			{
				settlementPerContract -= intrinsic;
				settlementLegs.Add(new BacktestLegFill(leg.Symbol, Side.Buy, leg.Qty, intrinsic));
			}
		}

		// Expirations don't incur broker commissions: cash-settled index options (SPX/SPXW/XSP/NDX)
		// settle automatically with no fee, and OTM equity options simply expire worthless. The
		// small assignment fee on ITM equity options is broker-specific and a rounding error
		// relative to the trade P&L — ignore it here to keep the model uniformly fee-free on expiry.
		var cashFlow = settlementPerContract * Multiplier * pos.Quantity;
		Cash += cashFlow;

		var lineageId = _lineageByKey[positionKey];
		_fills.Add(new BacktestFill(date, positionKey, pos.Ticker, pos.StrategyKind, pos.Quantity, BacktestFillKind.Expire,
			LineageId: lineageId, Legs: settlementLegs, NetCashFlow: cashFlow, Fees: 0m, RuleName: null));

		_positions.Remove(positionKey);
		_initialDebitPerContract.Remove(positionKey);
		_adjustedDebitPerContract.Remove(positionKey);
		_lineageByKey.Remove(positionKey);
		return true;
	}

	/// <summary>Per-leg fill → total dollar cash flow. Sell legs add credit; buy legs subtract debit.
	/// Each leg's contribution = ±PricePerShare × 100 × LegQty (the leg's own qty, which the runner
	/// has already set to the contract count).</summary>
	private static decimal ComputeCashFlow(IReadOnlyList<BacktestLegFill> legFills)
	{
		decimal total = 0m;
		foreach (var l in legFills)
		{
			var legNotional = l.PricePerShare * Multiplier * l.Qty;
			total += l.Side == Side.Sell ? legNotional : -legNotional;
		}
		return total;
	}

	private static IReadOnlyList<PositionLeg> BuildLegsFromFills(IReadOnlyList<BacktestLegFill> legFills, int qty, out string strategyKind, OpenStructureKind structureKind)
	{
		var result = new List<PositionLeg>();
		foreach (var l in legFills)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(l.Symbol);
			if (parsed == null) continue;
			result.Add(new PositionLeg(
				Symbol: l.Symbol,
				Side: l.Side,
				Strike: parsed.Strike,
				Expiry: parsed.ExpiryDate,
				CallPut: parsed.CallPut,
				Qty: qty));
		}
		strategyKind = structureKind.ToString();
		return result;
	}

	private static string ComputeKey(string ticker, string strategyKind, IReadOnlyList<PositionLeg> legs)
	{
		var shortLeg = legs.FirstOrDefault(l => l.Side == Side.Sell) ?? legs[0];
		var expiry = shortLeg.Expiry ?? DateTime.MinValue;
		return $"{ticker}_{strategyKind}_{shortLeg.Strike:F2}_{expiry:yyyyMMdd}";
	}
}
