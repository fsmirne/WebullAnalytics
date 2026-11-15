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
	/// <summary>The proposal handed out and not yet reported acted on: what <see cref="Acted"/> turns into the ledger's final entry.</summary>
	private (string Fingerprint, CascadeSignalLedger.Entry Entry)? _pending;

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
	public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation)
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
		if (!_quiet) CascadePanel.Render(ticker, nowEt, $"{read.ExpiryCount} expiration(s) ≤{CascadeScreener.DefaultDte}DTE", reading, showLegend: false);

		var setup = reading.PrimaryRegime;
		var trade = cfg.For(setup);
		if (trade is not { Enabled: true }) return Array.Empty<OpenProposal>();
		// Entry conditions (VIX, score) are checked before the ledger: a reading that fails one is not this setup's entry, so a later reading
		// of the day that passes may still be.
		if (trade.EntryConditionFailure(reading) is string gated)
		{
			GatedReadings.Add((setup, gated));
			return Array.Empty<OpenProposal>();
		}
		var today = DateOnly.FromDateTime(nowEt);
		// Done for the day once an entry is final (acted on, or a first confirmation with no trade) or the retry window has passed.
		var entries = _ledger.Find(today, ticker, setup, _live);
		var first = entries.FirstOrDefault();
		if (entries.Any(e => e.Outcome != CascadeSignalLedger.Signaled)) return Array.Empty<OpenProposal>();
		if (first != null && (nowEt.TimeOfDay - TimeSpan.Parse(first.TimeEt)).TotalSeconds > cfg.RetrySeconds) return Array.Empty<OpenProposal>();

		var (skeleton, skip) = BuildTrade(ticker, nowEt.Date, read.Spot, reading.Terrain, read.Quotes, _config.Indicators.StrikeStep, trade);
		var proposal = skeleton == null ? null : Propose(skeleton, read, ctx);
		if (skeleton != null && proposal == null) skip = "the scorer could not price the legs";

		var label = CascadeReading.RegimeLabel(setup);
		var entry = new CascadeSignalLedger.Entry(today.ToString("yyyy-MM-dd"), ticker, setup, nowEt.ToString("HH:mm:ss"), _live, reading.LeadingSetup.Score, read.Spot, CascadeSignalLedger.Signaled, proposal?.Legs.Describe() ?? skip!);
		if (proposal == null)
		{
			// A retry that cannot build the trade any more just waits out the window; only the first confirmation is final.
			if (first != null) return Array.Empty<OpenProposal>();
			_ledger.Append(entry with { Outcome = CascadeSignalLedger.Skipped });
			if (!_quiet) AnsiConsole.MarkupLine($"[yellow]cascade {Markup.Escape(label)}: no trade today[/] — {Markup.Escape(skip!)}. The first confirmation is the day's only entry.");
			return Array.Empty<OpenProposal>();
		}
		if (first == null) _ledger.Append(entry);
		else if (!_quiet) AnsiConsole.MarkupLine($"[dim]cascade {Markup.Escape(label)}: first confirmed at {Markup.Escape(first.TimeEt)} ET and not yet placed — proposing again.[/]");
		_pending = (proposal.Fingerprint, entry with { Outcome = CascadeSignalLedger.Placed });
		return new[] { proposal with { Rationale = $"cascade {label} {reading.LeadingSetup.Score}/100 — {proposal.Rationale}", Spot = read.Spot } };
	}

	/// <summary>Reports that <paramref name="proposal"/> (one this opener returned) was acted on — an order placed, or logged by a dry
	/// run — or was only ever for display. Its setup is then done for the day.</summary>
	public void Acted(OpenProposal proposal)
	{
		if (_pending is not { } pending || pending.Fingerprint != proposal.Fingerprint) return;
		_ledger.Append(pending.Entry);
		_pending = null;
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
	internal static (CandidateSkeleton? Skeleton, string? Skip) BuildTrade(string ticker, DateTime today, decimal spot, CascadeTerrain terrain, IReadOnlyDictionary<string, OptionContractQuote> quotes, decimal strikeStep, CascadeSetupConfig cfg)
	{
		if (cfg.Kind is not OpenStructureKind kind) return (null, $"'{cfg.Structure}' is not a structure a cascade setup can open");
		var (firstStrike, secondStrike, noStrikes) = TradeStrikes(spot, terrain, strikeStep, cfg);
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
	internal static (decimal First, decimal? Second, string? Skip) TradeStrikes(decimal spot, CascadeTerrain terrain, decimal strikeStep, CascadeSetupConfig cfg)
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
