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
	string? RuleName,      // null for Open, else the management rule that fired
	decimal Spot           // underlying price at the fill — lets the ledger compare strikes vs spot
);

internal sealed record BacktestLegFill(
	string Symbol,
	Side Side,           // Buy or Sell (the executed action, not the original leg direction)
	int Qty,
	decimal PricePerShare
);

internal enum BacktestFillKind { Open, Close, Roll, Expire, LegIn }

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

	// Cash-settled index roots share the engine's single source of truth (these are exactly the
	// European, cash-settled indexes — the same set that carries no early-assignment risk in scoring).
	private static readonly IReadOnlySet<string> IndexOptionTickers = OptionSettlement.CashSettledIndexRoots;

	// Per-ticker fee overrides where the broker rate differs from the $1.14 index default, verified
	// against live Webull fills: XSP (Mini-SPX) is $0.55, NDX is $1.30. SPX/SPXW stay at $1.14;
	// RUT/DJX/VIX are unverified and fall back to the $1.14 index default.
	private static readonly Dictionary<string, decimal> FeePerContractOverrides = new(StringComparer.OrdinalIgnoreCase)
	{
		["XSP"] = 0.55m,
		["NDX"] = 1.30m,
	};

	/// <summary>Per-leg-contract commission default for <paramref name="ticker"/>. Per-ticker overrides
	/// (XSP $0.55, NDX $1.30) win; other cash-settled index options charge $1.14; equity/ETF options
	/// (incl. IWM) charge $0.05.</summary>
	public static decimal DefaultFeePerContractFor(string ticker) =>
		FeePerContractOverrides.TryGetValue(ticker, out var fee) ? fee
		: IndexOptionTickers.Contains(ticker) ? IndexOptionFeePerContract
		: EquityOptionFeePerContract;

	private readonly Dictionary<string, OpenPosition> _positions = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, decimal> _initialDebitPerContract = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, decimal> _adjustedDebitPerContract = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _lineageByKey = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<BacktestFill> _fills = new();
	private long _nextLineageId = 1;

	private readonly decimal _feePerContract;
	private readonly OpenerRealizedExpectancyConfig _slippageConfig;

	/// <summary>Per-leg-contract commission. Exposed so research paths (oracle EOD P&L estimator)
	/// can include the same fee model the simulator uses on real Open fills.</summary>
	public decimal FeePerContract => _feePerContract;

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

	/// <summary>Realized P&L = cash flow on closed/expired positions only. Open positions' mark-to-market is excluded.</summary>
	public decimal RealizedPnL => _fills.Sum(f => f.NetCashFlow - f.Fees);

	public decimal TotalFees => _fills.Sum(f => f.Fees);

	public int OpenCount => _positions.Count;

	/// <param name="legFills">Per-leg fills. <c>PricePerShare</c> must be set; <c>Side</c> is the executed direction.</param>
	public bool Open(DateTime date, string ticker, OpenStructureKind structureKind, IReadOnlyList<BacktestLegFill> legFills, int qty, decimal spot)
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

		_fills.Add(new BacktestFill(date, key, ticker, strategyKind, qty, BacktestFillKind.Open, lineageId, legFills, cashFlow, fees, null, spot));
		return true;
	}

	public bool Close(DateTime date, string positionKey, IReadOnlyList<BacktestLegFill> legFills, string ruleName, decimal spot)
	{
		if (!_positions.TryGetValue(positionKey, out var pos)) return false;

		var structureKind = Enum.Parse<OpenStructureKind>(pos.StrategyKind, ignoreCase: true);
		var cashFlow = ComputeCashFlow(legFills) - ComputeSlippage(structureKind, pos.Quantity);
		var fees = Math.Abs(pos.Quantity) * legFills.Count * _feePerContract;
		Cash += cashFlow - fees;

		var lineageId = _lineageByKey[positionKey];
		_fills.Add(new BacktestFill(date, positionKey, pos.Ticker, pos.StrategyKind, pos.Quantity, BacktestFillKind.Close, lineageId, legFills, cashFlow, fees, ruleName, spot));

		_positions.Remove(positionKey);
		_initialDebitPerContract.Remove(positionKey);
		_adjustedDebitPerContract.Remove(positionKey);
		_lineageByKey.Remove(positionKey);
		return true;
	}

	public bool Roll(DateTime date, string oldPositionKey, IReadOnlyList<BacktestLegFill> legFills, string ruleName, decimal spot, OpenStructureKind? newStructureKind = null)
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

		_fills.Add(new BacktestFill(date, newKey, oldPos.Ticker, strategyKind, oldPos.Quantity, BacktestFillKind.Roll, lineageId, legFills, cashFlow, fees, ruleName, spot));
		return true;
	}

	/// <summary>Append one or more new legs to an existing position (sell-to-open or buy-to-open),
	/// without closing any existing legs. Used by <c>LegInShortRule</c> to convert a single-leg long
	/// (LongCall/LongPut) into a vertical by adding a higher/lower-strike short. Cash flow comes
	/// from the new legs only; the existing legs and their basis are preserved. Resulting strategy
	/// kind is supplied by the caller (e.g., LongCallVertical) and the position key recomputes off
	/// the new short leg.</summary>
	public bool LegIn(DateTime date, string oldPositionKey, IReadOnlyList<BacktestLegFill> newLegs, string ruleName, OpenStructureKind newStructureKind, decimal spot)
	{
		if (!_positions.TryGetValue(oldPositionKey, out var oldPos)) return false;
		if (newLegs.Count == 0) return false;

		// One combo execution = one slippage cross. Fee scales with new-leg count only — existing
		// legs aren't re-executed.
		var cashFlow = ComputeCashFlow(newLegs) - ComputeSlippage(newStructureKind, oldPos.Quantity);
		var fees = Math.Abs(oldPos.Quantity) * newLegs.Count * _feePerContract;
		Cash += cashFlow - fees;

		// Build merged leg list: existing legs unchanged, new legs appended.
		var mergedLegs = new List<PositionLeg>(oldPos.Legs);
		foreach (var f in newLegs)
		{
			var parsed = ParsingHelpers.ParseOptionSymbol(f.Symbol);
			if (parsed == null) continue;
			mergedLegs.Add(new PositionLeg(
				Symbol: f.Symbol,
				Side: f.Side,
				Strike: parsed.Strike,
				Expiry: parsed.ExpiryDate,
				CallPut: parsed.CallPut,
				Qty: oldPos.Quantity));
		}
		if (mergedLegs.Count == oldPos.Legs.Count) return false; // nothing parsed

		var strategyKind = newStructureKind.ToString();
		var newKey = ComputeKey(oldPos.Ticker, strategyKind, mergedLegs);

		// Basis adjustment: incoming credit (negative cashFlow sign convention from ComputeCashFlow:
		// sell-to-open is positive cashFlow → debit reduces). Same per-share normalization as Roll.
		var initialDebit = _initialDebitPerContract[oldPositionKey];
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
			Legs: mergedLegs,
			InitialNetDebit: initialDebit,
			AdjustedNetDebit: adjustedDebit,
			Quantity: oldPos.Quantity,
			OpenedAt: oldPos.OpenedAt,
			MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebit, mergedLegs));
		_initialDebitPerContract[newKey] = initialDebit;
		_adjustedDebitPerContract[newKey] = adjustedDebit;
		_lineageByKey[newKey] = lineageId;

		_fills.Add(new BacktestFill(date, newKey, oldPos.Ticker, strategyKind, oldPos.Quantity, BacktestFillKind.LegIn, lineageId, newLegs, cashFlow, fees, ruleName, spot));
		return true;
	}

	/// <summary>Settle only the legs of a position that have expired on or before <paramref name="date"/>, at
	/// intrinsic against <paramref name="spotAtExpiry"/>. If every leg has expired the lineage terminates (the
	/// common case — a 0DTE structure or a calendar closed past its long expiry). If future-dated legs survive
	/// (a calendar/diagonal whose short leg expired while the long leg lives on), the position stays OPEN on the
	/// survivors: it is re-keyed (the key is leg-derived) with the lineage carried forward, and the per-share
	/// basis is reduced by the settled legs' cash so rule thresholds on the survivor stay correct.
	///
	/// <para>Settling the whole structure at intrinsic on the short leg's expiry day — the prior behavior — was
	/// wrong for calendars/diagonals: it discarded the long leg's remaining extrinsic value, one-directionally
	/// penalizing the strategy exactly on the good outcome where the short expires worthless.</para></summary>
	/// <returns>The re-keyed survivor's key when a partial expiry leaves future-dated legs open (so the
	/// caller can close them at the same day's close instead of carrying them overnight); otherwise null
	/// (position not found, nothing expired, or full expiry that terminated the lineage).</returns>
	public string? Expire(DateTime date, string positionKey, decimal spotAtExpiry)
	{
		if (!_positions.TryGetValue(positionKey, out var pos)) return null;

		var expiredLegs = new List<PositionLeg>();
		var survivingLegs = new List<PositionLeg>();
		foreach (var leg in pos.Legs)
		{
			if (leg.Expiry.HasValue && leg.Expiry.Value.Date <= date.Date) expiredLegs.Add(leg);
			else survivingLegs.Add(leg);
		}
		if (expiredLegs.Count == 0) return null;

		// Settle the expired legs at intrinsic: long legs collect it, short legs pay it.
		var settlementPerContract = 0m;
		var settlementLegs = new List<BacktestLegFill>();
		foreach (var leg in expiredLegs)
		{
			if (leg.CallPut == null) continue;
			var intrinsic = leg.CallPut == "C" ? Math.Max(0m, spotAtExpiry - leg.Strike) : Math.Max(0m, leg.Strike - spotAtExpiry);
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
			LineageId: lineageId, Legs: settlementLegs, NetCashFlow: cashFlow, Fees: 0m, RuleName: null, Spot: spotAtExpiry));

		// Full expiry: nothing survives → terminate the lineage.
		if (survivingLegs.Count == 0)
		{
			_positions.Remove(positionKey);
			_initialDebitPerContract.Remove(positionKey);
			_adjustedDebitPerContract.Remove(positionKey);
			_lineageByKey.Remove(positionKey);
			return null;
		}

		// Partial expiry: carry the surviving legs forward as a re-keyed open position. Basis adjustment
		// mirrors Roll/LegIn — new basis = old basis − settled-leg cash per share (settling a worthless short
		// adds 0; settling an ITM short you must pay for raises the basis you have to recover from the long).
		var newStrategyKind = DeriveSurvivingStrategyKind(survivingLegs, pos.StrategyKind);
		var newKey = ComputeKey(pos.Ticker, newStrategyKind, survivingLegs);
		var initialDebit = _initialDebitPerContract[positionKey] - settlementPerContract;
		var adjustedDebit = _adjustedDebitPerContract[positionKey] - settlementPerContract;

		_positions.Remove(positionKey);
		_initialDebitPerContract.Remove(positionKey);
		_adjustedDebitPerContract.Remove(positionKey);
		_lineageByKey.Remove(positionKey);

		// Defensive: a surviving leg set that collides with another open position's key is vanishingly
		// unlikely, but if it happens, drop the survivor rather than clobber the existing lineage.
		if (_positions.ContainsKey(newKey)) return null;

		_positions[newKey] = new OpenPosition(
			Key: newKey,
			Ticker: pos.Ticker,
			StrategyKind: newStrategyKind,
			Legs: survivingLegs,
			InitialNetDebit: initialDebit,
			AdjustedNetDebit: adjustedDebit,
			Quantity: pos.Quantity,
			OpenedAt: pos.OpenedAt,
			MaxLossPerShare: PositionRiskEstimator.MaxLossPerShare(initialDebit, survivingLegs));
		_initialDebitPerContract[newKey] = initialDebit;
		_adjustedDebitPerContract[newKey] = adjustedDebit;
		_lineageByKey[newKey] = lineageId;
		return newKey;
	}

	/// <summary>Strategy-kind for a partially-settled position's surviving legs. A single surviving long option
	/// (the calendar/diagonal case once the short expires) becomes LongCall/LongPut — accurate for rule logic and
	/// for the OpenStructureKind parse in <see cref="Close"/>. Anything else keeps the original kind (functional:
	/// the leg set, not the label, drives rule evaluation).</summary>
	private static string DeriveSurvivingStrategyKind(IReadOnlyList<PositionLeg> survivors, string fallback)
	{
		if (survivors.Count == 1 && survivors[0].Side == Side.Buy && survivors[0].CallPut != null)
			return survivors[0].CallPut == "C" ? nameof(OpenStructureKind.LongCall) : nameof(OpenStructureKind.LongPut);
		return fallback;
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
		// CallPut disambiguates the two same-strike halves of a side-split double (--split): both book as
		// e.g. LongCalendar at the same short strike/expiry and would otherwise collide, silently dropping one.
		return $"{ticker}_{strategyKind}_{shortLeg.Strike:F2}{shortLeg.CallPut}_{expiry:yyyyMMdd}";
	}
}
