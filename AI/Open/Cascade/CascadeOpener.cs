using Spectre.Console;
using WebullAnalytics.Analyze;
using WebullAnalytics.Utils;

namespace WebullAnalytics.AI.Open.Cascade;

/// <summary>
/// Opens one configured 0DTE trade per confirmed setup of the live `analyze cascade` regime read (<see cref="OpenerCascadeConfig"/>),
/// outside candidate enumeration and ranking. Each of the four setups (Bull Trap, Bear Trap, Bull Cascade, Bear Cascade) names its
/// own trade in a <see cref="CascadeSetupConfig"/>: a structure, the strike its first leg sits at, and for a vertical the width.
/// Exits are the management rules' business (<c>rules.trailingStop</c>, with structures opted out via <c>rules.trailingStop.exclude</c>; none configured = held to expiry).
///
/// A setup trades once a day, at its first confirmation (<see cref="CascadeSignalLedger"/>): a first confirmation that yields no
/// trade (no wall out of the money, a credit under the floor) uses up the day's signal. One that yields a trade is used up when the
/// auto-executor acts on it (<see cref="Acted"/>); if the executor held it back (quote guard, broker refresh failure, cash), the
/// setup is proposed again on later reads while it stays confirmed, until <see cref="OpenerCascadeConfig.RetrySeconds"/> after the
/// first confirmation. Nothing was opened in that case, so a retry can never re-enter a position the stop already closed. The
/// proposal is priced from the chain the reading was scored on and sized by the opener's own cash and risk caps.
///
/// The neutral setup (<see cref="CascadeNeutralConfig"/>) is the absence of the others: from its time on, on a day no regime has
/// confirmed, each of its two sides trades once under the same rules. Every regime's first confirmation of the day is recorded in the
/// ledger, so a restarted loop knows the day is not neutral.
///
/// The reading runs on its own clock (<see cref="OpenerCascadeConfig.ReadSeconds"/>), not on every watch tick.
/// </summary>
internal sealed class CascadeOpener
{
	/// <summary>One live cascade read at an ET instant: the reading and its chain, or the reason there is none as console markup.</summary>
	internal delegate Task<(CascadeLiveRead? Read, string? Error)> Reader(DateTime nowEt, CancellationToken cancellation);

	private readonly AIConfig _config;
	private readonly bool _live;
	private readonly CascadeSignalLedger _ledger;
	private readonly Reader _reader;
	private readonly bool _quiet;
	private readonly Func<DateTime, DateTime> _toEt;
	private DateTime? _lastReadAt;
	/// <summary>Proposals handed out and not yet reported acted on, by fingerprint: what <see cref="Acted"/> turns into each one's final entry.</summary>
	private readonly Dictionary<string, CascadeSignalLedger.Entry> _pending = new(StringComparer.Ordinal);
	/// <summary>Regimes this process has already recorded as confirmed, per day, so the ledger is written once per regime and day.</summary>
	private readonly HashSet<(DateOnly Day, CascadeRegime Regime)> _confirmed = new();

	/// <param name="live">True when the opener's auto-executor places real orders; a dry run keeps its own ledger entries.</param>
	/// <param name="quiet">Print nothing (no panel, no notices): a backtest replays thousands of readings under a progress bar.</param>
	/// <param name="toEt">Maps the evaluation context's clock to ET wall time. Live contexts run on the machine's clock (the default
	/// converts it); a backtest's are already ET and pass the identity.</param>
	public CascadeOpener(AIConfig config, bool live, CascadeSignalLedger ledger, Reader reader, bool quiet = false, Func<DateTime, DateTime>? toEt = null)
	{
		_config = config;
		_live = live;
		_ledger = ledger;
		_reader = reader;
		_quiet = quiet;
		_toEt = toEt ?? MarketTime.ToEt;
	}

	/// <summary>Confirmed readings an entry condition held back, with the condition: the backtest's summary counts them.</summary>
	public List<(CascadeRegime Setup, string Reason)> GatedReadings { get; } = new();

	/// <summary>The vendor-reported spot of the most recent reading; null before the first one.</summary>
	public decimal? LastSpot { get; private set; }

	/// <summary>The chain the most recent reading was scored on — the book this opener's proposals were priced from.</summary>
	public IReadOnlyDictionary<string, OptionContractQuote> LastQuotes { get; private set; } = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);

	/// <summary>Reads the regime when a read is due and returns the trade of a setup confirming for the first time today, if any.</summary>
	/// <param name="entryWindowOpen">False outside the opener's entry window: the reading is still taken and shown, but nothing is recorded or
	/// traded, so the first confirmation inside the window is the one that trades.</param>
	public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation, bool entryWindowOpen = true)
	{
		var cfg = _config.Opener.Cascade;
		if (!cfg.Enabled) return Array.Empty<OpenProposal>();
		if (_lastReadAt is DateTime last && (ctx.Now - last).TotalSeconds < cfg.ReadSeconds) return Array.Empty<OpenProposal>();
		_lastReadAt = ctx.Now;

		var nowEt = _toEt(ctx.Now);
		var (read, error) = await _reader(nowEt, cancellation);
		if (read == null)
		{
			if (!_quiet) AnsiConsole.MarkupLine($"[yellow]cascade read failed:[/] {error}");
			return Array.Empty<OpenProposal>();
		}
		LastQuotes = read.Quotes;
		LastSpot = read.Spot;

		var reading = read.Reading;
		var ticker = _config.Ticker;
		var setup = reading.PrimaryRegime;
		var today = DateOnly.FromDateTime(nowEt);
		var trades = new List<CascadeTradeSection.Trade>();
		var notices = new List<string>();
		if (!entryWindowOpen)
		{
			notices.Add($"[dim]outside the opener entry window ({Markup.Escape(_config.Opener.EarliestEntryTimeEt ?? "open")}–{Markup.Escape(_config.Opener.LatestEntryTimeEt ?? "close")} ET): reading only, no confirmation recorded, nothing traded[/]");
			if (!_quiet) CascadePanel.Render(ticker, nowEt, $"{read.ExpiryCount} expiration(s) ≤{CascadeScreener.DefaultDte}DTE", reading, showLegend: false, trade: CascadeTradeSection.Build(trades, notices, Today(today, ticker, nowEt), _live));
			return Array.Empty<OpenProposal>();
		}

		if (setup != CascadeRegime.Neutral) RecordConfirmation(today, ticker, setup, nowEt, reading, read.Spot);
		if (setup != CascadeRegime.Neutral)
		{
			if (cfg.For(setup) is { Enabled: true } trade) TryEntry(setup, null, trade, read, ctx, nowEt, trades, notices);
		}
		else
		{
			// A reading that leans one way may trade that setup, when its block allows leans and the lean is strong enough.
			if (LeanSetup(reading) is { } lean) TryEntry(lean.Regime, null, lean.Trade, read, ctx, nowEt, trades, notices, leanScore: reading.LeadingSetup.Score);
			if (NeutralDue(cfg.Neutral, today, ticker, nowEt))
			{
				foreach (var (side, trade) in cfg.Neutral.EnabledSides) TryEntry(CascadeRegime.Neutral, side, trade, read, ctx, nowEt, trades, notices);
			}
		}

		// The reading, what it is opening and the day so far, in one panel.
		if (!_quiet) CascadePanel.Render(ticker, nowEt, $"{read.ExpiryCount} expiration(s) ≤{CascadeScreener.DefaultDte}DTE", reading, showLegend: false, trade: CascadeTradeSection.Build(trades, notices, Today(today, ticker, nowEt), _live));
		return trades.Select(t => t.Proposal).ToList();
	}

	/// <summary>What became of each enabled setup (and neutral side) today in this mode, from the ledger: one row each, in config order.</summary>
	private IReadOnlyList<CascadeTradeSection.Outcome> Today(DateOnly today, string ticker, DateTime nowEt)
	{
		var cfg = _config.Opener.Cascade;
		var rows = new List<CascadeTradeSection.Outcome>();
		foreach (var regime in new[] { CascadeRegime.BullTrap, CascadeRegime.BearTrap, CascadeRegime.BullCascade, CascadeRegime.BearCascade })
			if (cfg.For(regime) is { Enabled: true }) rows.Add(OutcomeOf(today, ticker, regime, null, nowEt));
		if (cfg.Neutral.Enabled)
			foreach (var (side, _) in cfg.Neutral.EnabledSides) rows.Add(OutcomeOf(today, ticker, CascadeRegime.Neutral, side, nowEt));
		return rows;
	}

	/// <param name="nowEt">Decides whether a confirmation not yet placed is still inside its retry window.</param>
	private CascadeTradeSection.Outcome OutcomeOf(DateOnly today, string ticker, CascadeRegime regime, string? side, DateTime nowEt)
	{
		var label = side != null ? $"{CascadeReading.RegimeLabel(regime)} {side}" : CascadeReading.RegimeLabel(regime);
		var entries = _ledger.Find(today, ticker, regime, _live, side);
		var final = entries.LastOrDefault(e => e.Outcome is CascadeSignalLedger.Placed or CascadeSignalLedger.Skipped) ?? entries.LastOrDefault();
		TimeSpan? retryUntil = null;
		if (final is { Outcome: CascadeSignalLedger.Signaled } && TimeSpan.TryParse(final.TimeEt, out var signaledAt))
		{
			var until = signaledAt + TimeSpan.FromSeconds(_config.Opener.Cascade.RetrySeconds);
			if (nowEt.TimeOfDay <= until) retryUntil = until;
		}
		return new CascadeTradeSection.Outcome(label, final, retryUntil);
	}

	/// <summary>The setup an unconfirmed <paramref name="reading"/> leans to, when that setup is enabled and its <see cref="CascadeSetupConfig.MinLeanScore"/>
	/// is set and met; null otherwise.</summary>
	internal (CascadeRegime Regime, CascadeSetupConfig Trade)? LeanSetup(CascadeReading reading)
	{
		var lead = reading.LeadingSetup;
		return reading.PrimaryRegime == CascadeRegime.Neutral && _config.Opener.Cascade.For(lead.Regime) is { Enabled: true, MinLeanScore: > 0 } trade && lead.Score >= trade.MinLeanScore
			? (lead.Regime, trade)
			: null;
	}

	/// <summary>True when the neutral setup may trade at <paramref name="nowEt"/>: enabled, at or past its time, and no regime has confirmed today.</summary>
	private bool NeutralDue(CascadeNeutralConfig neutral, DateOnly today, string ticker, DateTime nowEt) =>
		neutral.Enabled
		&& ParsingHelpers.TryParseClockTime(neutral.AfterTimeEt, out var after) && nowEt.TimeOfDay >= after
		&& !_confirmed.Any(c => c.Day == today)
		&& _ledger.ConfirmedRegimes(today, ticker).Count == 0;

	/// <summary>Records the regime's first confirmation of the day, once per process and day (the ledger keeps it across restarts).</summary>
	private void RecordConfirmation(DateOnly today, string ticker, CascadeRegime regime, DateTime nowEt, CascadeReading reading, decimal spot)
	{
		if (!_confirmed.Add((today, regime))) return;
		if (_ledger.ConfirmedRegimes(today, ticker).Contains(regime)) return;
		_ledger.Append(new CascadeSignalLedger.Entry(today.ToString("yyyy-MM-dd"), ticker, regime, nowEt.ToString("HH:mm:ss"), _live, reading.LeadingSetup.Score, spot, CascadeSignalLedger.Confirmed, ""));
	}

	/// <summary>Adds the setup's (or neutral side's) trade on this reading to <paramref name="trades"/>, if it is due: entry conditions met, not
	/// yet final today, within its retry window, and buildable. Otherwise the ledger is updated and the reason, when worth showing, goes to
	/// <paramref name="notices"/> (console markup).</summary>
	/// <param name="leanScore">Set when the reading did not confirm and this is a lean entry: the leading setup's score, shown in the notices.</param>
	private void TryEntry(CascadeRegime setup, string? side, CascadeSetupConfig trade, CascadeLiveRead read, EvaluationContext ctx, DateTime nowEt, List<CascadeTradeSection.Trade> trades, List<string> notices, int? leanScore = null)
	{
		var reading = read.Reading;
		var ticker = _config.Ticker;
		var label = side != null ? $"{CascadeReading.RegimeLabel(setup)} {side}" : leanScore is int lean ? $"{CascadeReading.RegimeLabel(setup)} (lean {lean})" : CascadeReading.RegimeLabel(setup);
		// Entry conditions (VIX, score) are checked before the ledger: a reading that fails one is not this setup's entry, so a later reading
		// of the day that passes may still be.
		if (trade.EntryConditionFailure(reading) is string gated)
		{
			GatedReadings.Add((setup, gated));
			return;
		}
		var today = DateOnly.FromDateTime(nowEt);
		// Done for the day once an entry is final (acted on, or a first confirmation with no trade) or the retry window has passed.
		var entries = _ledger.Find(today, ticker, setup, _live, side);
		var first = entries.FirstOrDefault(e => e.Outcome == CascadeSignalLedger.Signaled);
		if (entries.Any(e => e.Outcome is CascadeSignalLedger.Placed or CascadeSignalLedger.Skipped)) return;
		if (first != null && (nowEt.TimeOfDay - TimeSpan.Parse(first.TimeEt)).TotalSeconds > _config.Opener.Cascade.RetrySeconds) return;

		var (skeleton, skip) = BuildTrade(ticker, nowEt.Date, read.Spot, reading.Terrain, read.Quotes, _config.Indicators.StrikeStep, trade, reading.Inputs.Tape);
		var proposal = skeleton == null ? null : Propose(skeleton, read, ctx);
		if (skeleton != null && proposal == null) skip = "the scorer could not price the legs";

		var entry = new CascadeSignalLedger.Entry(today.ToString("yyyy-MM-dd"), ticker, setup, nowEt.ToString("HH:mm:ss"), _live, reading.LeadingSetup.Score, read.Spot, CascadeSignalLedger.Signaled, proposal?.Legs.Describe() ?? skip!, side);
		// The trade as proposed: what it is, its size and the price it was proposed at. A trade the sizing caps zero out keeps the unscaled
		// one-lot skeleton as its legs, so the signal records its legs at the size it was given (x0) and why.
		if (proposal != null)
			entry = entry with
			{
				Detail = proposal.Qty >= 1 ? proposal.Legs.Describe() : proposal.Legs.Select(l => l with { Qty = 0 }).Describe(),
				Structure = proposal.StructureKind.ToString(), Qty = proposal.Qty, NetPerShare = proposal.DebitOrCreditPerContract / 100m,
				Note = proposal.Qty >= 1 ? null : proposal.CashReserveDetail ?? "the sizing caps allow none",
				Commands = proposal.Qty >= 1 ? OpenerAutoExecutor.PlaceCommands(proposal, BrokerStopManager.ConfigFor(_config)) : null,
			};
		if (proposal == null)
		{
			// A retry that cannot build the trade any more just waits out the window; only the first confirmation is final.
			if (first != null) return;
			_ledger.Append(entry with { Outcome = CascadeSignalLedger.Skipped });
			notices.Add($"[yellow]{Markup.Escape(label)}: no trade today[/] — {Markup.Escape(skip!)}. The first confirmation is the day's only entry.");
			return;
		}
		if (first == null) _ledger.Append(entry);
		else notices.Add($"[dim]{Markup.Escape(label)}: first confirmed at {Markup.Escape(first.TimeEt)} ET and not yet placed — proposing again.[/]");
		_pending[proposal.Fingerprint] = entry with { Outcome = CascadeSignalLedger.Placed, Detail = proposal.Legs.Describe() };
		trades.Add(new CascadeTradeSection.Trade(proposal with { Rationale = $"cascade {label} {reading.LeadingSetup.Score}/100 — {proposal.Rationale}", Spot = read.Spot }, $"{label} {reading.LeadingSetup.Score}/100"));
	}

	/// <summary>Reports that <paramref name="proposal"/> (one this opener returned) was acted on — an order placed, or logged by a dry
	/// run — or was only ever for display. Its setup is then done for the day.</summary>
	public void Acted(OpenProposal proposal)
	{
		if (_pending.Remove(proposal.Fingerprint, out var entry)) _ledger.Append(entry);
	}

	/// <summary>Prices the skeleton on the reading's chain (the scorer's liquidity and veto gates off: the signal picked the trade)
	/// and sizes it by the opener's cash and risk caps. Null when the scorer cannot price a leg.</summary>
	private OpenProposal? Propose(CandidateSkeleton skeleton, CascadeLiveRead read, EvaluationContext ctx)
	{
		var hv = ctx.HistoricalVolByTicker != null && ctx.HistoricalVolByTicker.TryGetValue(skeleton.Ticker, out var h) && h > 0m ? h : (decimal?)null;
		var scored = CandidateScorer.Score(skeleton, read.Spot, ctx.Now, read.Quotes, 0m, _config.Opener, hv, applyLiquidityGate: false);
		return scored == null ? null : OpenCandidateEvaluator.ApplyCashSizing(scored, OpenCandidateEvaluator.FreeCash(_config, ctx), ctx.AccountValue, _config.Opener, 0m);
	}

	/// <summary>The trade <paramref name="cfg"/> names, from the options expiring today: the first leg at the anchor strike (bought, or
	/// sold for a credit vertical) and, for a vertical, the opposite leg <see cref="CascadeSetupConfig.WidthSteps"/> strike steps further
	/// out of the money. Returns the reason when there is no such trade.</summary>
	internal static (CandidateSkeleton? Skeleton, string? Skip) BuildTrade(string ticker, DateTime today, decimal spot, CascadeTerrain terrain, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal strikeStep, CascadeSetupConfig cfg, AI.Open.ZeroDte.SessionTape? tape = null)
	{
		if (cfg.Kind is not OpenStructureKind kind) return (null, $"'{cfg.Structure}' is not a structure a cascade setup can open");
		var (firstStrike, secondStrike, noStrikes) = TradeStrikes(spot, terrain, strikeStep, cfg, tape);
		if (noStrikes != null) return (null, noStrikes);
		var right = cfg.Right;
		var name = right == "C" ? "call" : "put";

		// The first leg must trade both ways: its mid prices the entry, and a long option's bid is what the trailing stop reads.
		var first = ExpiringOption(ticker, today, firstStrike, right, quotes);
		if (first == null) return (null, $"no {name} expiring today at ${firstStrike:N0}");
		if (first.Bid is not > 0m || first.Ask is not > 0m) return (null, $"no two-sided quote on the ${firstStrike:N0} {name}");
		var firstAction = cfg.IsCredit ? "sell" : "buy";
		if (!cfg.IsVertical)
			return (new CandidateSkeleton(ticker, kind, new[] { new ProposalLeg(firstAction, first.ContractSymbol, 1) }, today), null);

		var second = ExpiringOption(ticker, today, secondStrike!.Value, right, quotes);
		if (second == null) return (null, $"no {name} expiring today at ${secondStrike:N0}");
		// A bought wing needs only an offer (its bid is often 0); a sold one needs a bid.
		if (second.Ask is not > 0m || (!cfg.IsCredit && second.Bid is not > 0m)) return (null, $"no two-sided quote on the ${firstStrike:N0}/${secondStrike:N0} {name} spread");

		if (cfg.IsCredit)
		{
			var credit = (first.Bid.Value + first.Ask.Value) / 2m - ((second.Bid ?? 0m) + second.Ask.Value) / 2m;
			if (credit < cfg.MinCredit) return (null, $"${firstStrike:N0}/${secondStrike:N0} {name} spread mid credit ${credit:F2} is under minCredit ${cfg.MinCredit:F2}");
		}
		var legs = new[] { new ProposalLeg(firstAction, first.ContractSymbol, 1), new ProposalLeg(cfg.IsCredit ? "buy" : "sell", second.ContractSymbol, 1) };
		return (new CandidateSkeleton(ticker, kind, legs, today), null);
	}

	/// <summary>The strikes of the trade <paramref name="cfg"/> names — the first leg at the anchor (shifted by its offset) and, for a
	/// vertical, the second leg <see cref="CascadeSetupConfig.WidthSteps"/> steps further out of the money — or the reason there are none
	/// (no wall, or a wall that is not out of the money). Whether those strikes are listed and quoted is <see cref="BuildTrade"/>'s check.</summary>
	/// <param name="tape">The session so far; the session anchors read its high and low.</param>
	internal static (decimal First, decimal? Second, string? Skip) TradeStrikes(decimal spot, CascadeTerrain terrain, decimal strikeStep, CascadeSetupConfig cfg, AI.Open.ZeroDte.SessionTape? tape = null)
	{
		var right = cfg.Right;
		var outward = right == "C" ? strikeStep : -strikeStep;   // one strike step further out of the money

		decimal anchor;
		if (cfg.AnchorName == CascadeSetupConfig.AnchorAtm)
			anchor = (right == "C" ? Math.Ceiling(spot / strikeStep) : Math.Floor(spot / strikeStep)) * strikeStep;
		else
		{
			var (wall, label) = cfg.AnchorName switch
			{
				CascadeSetupConfig.AnchorCallWallAtHigh => (terrain.CallWallAtHigh0Dte, "call wall at the session high"),
				CascadeSetupConfig.AnchorPutWallAtLow => (terrain.PutWallAtLow0Dte, "put wall at the session low"),
				CascadeSetupConfig.AnchorCallWall => (terrain.Front.CallWall, "call wall"),
				CascadeSetupConfig.AnchorPutWall => (terrain.Front.PutWall, "put wall"),
				// The first listed strike at or beyond the extreme: above the high for a call, below the low for a put.
				CascadeSetupConfig.AnchorSessionHigh => (tape is { } hi ? Math.Ceiling(hi.SessionHigh / strikeStep) * strikeStep : (decimal?)null, "strike at the session high"),
				CascadeSetupConfig.AnchorSessionLow => (tape is { } lo ? Math.Floor(lo.SessionLow / strikeStep) * strikeStep : (decimal?)null, "strike at the session low"),
				_ => ((decimal?)null, $"'{cfg.Anchor}' anchor"),
			};
			if (wall is not decimal w) return (0m, null, $"no {label}");
			if (right == "C" ? w <= spot : w >= spot) return (0m, null, $"the {label} (${w:N0}) is not {(right == "C" ? "above" : "below")} spot ${spot:N2}");
			anchor = w;
		}
		var first = anchor + cfg.OffsetSteps * outward;
		return (first, cfg.IsVertical ? first + cfg.WidthSteps * outward : null, null);
	}

	/// <summary>The option of exactly this root expiring today at this strike. The root must match exactly: on a monthly expiry the AM-settled
	/// SPX series shares the date with SPXW, and it stopped trading the day before.</summary>
	private static OptionContractQuote? ExpiringOption(string ticker, DateTime today, decimal strike, string right, IReadOnlyDictionary<string, OptionContractQuote> quotes)
	{
		foreach (var q in quotes.Values)
		{
			var p = ParsingHelpers.ParseOptionSymbol(q.ContractSymbol);
			if (p != null && p.CallPut == right && p.Strike == strike && p.ExpiryDate.Date == today && string.Equals(p.Root, ticker, StringComparison.OrdinalIgnoreCase)) return q;
		}
		return null;
	}
}
