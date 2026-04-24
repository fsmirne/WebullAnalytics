using WebullAnalytics.AI.Sources;

namespace WebullAnalytics.AI;

/// <summary>
/// Orchestrates the opener pipeline: enumerate skeletons → phase-3 quote fetch → score → rank per ticker.
/// Produces a flat, ranked list of OpenProposal across all configured tickers, capped at topNPerTicker each.
/// </summary>
internal sealed class OpenCandidateEvaluator
{
    private readonly AIConfig _config;
    private readonly IQuoteSource _quotes;

    public OpenCandidateEvaluator(AIConfig config, IQuoteSource quotes)
    {
        _config = config;
        _quotes = quotes;
    }

    public async Task<IReadOnlyList<OpenProposal>> EvaluateAsync(EvaluationContext ctx, CancellationToken cancellation)
    {
        var cfg = _config.Opener;
        if (!cfg.Enabled) return Array.Empty<OpenProposal>();

        var tickerSet = new HashSet<string>(_config.Tickers, StringComparer.OrdinalIgnoreCase);
        var output = new List<OpenProposal>();

        // Phase A0: bootstrap spots + chains for tickers missing from ctx.UnderlyingPrices.
        // The live-quote clients return the full chain plus the underlying spot for any OCC symbol,
        // so one placeholder per root suffices. Without this, tickers without open positions have
        // no spot in ctx and we enumerate nothing.
        var bootstrapSpots = new Dictionary<string, decimal>(ctx.UnderlyingPrices, StringComparer.OrdinalIgnoreCase);
        var bootstrapOptions = new Dictionary<string, OptionContractQuote>(StringComparer.OrdinalIgnoreCase);
        var missingTickers = _config.Tickers.Where(t => !bootstrapSpots.ContainsKey(t) || bootstrapSpots[t] <= 0m).ToList();
        if (missingTickers.Count > 0)
        {
            var placeholderExpiry = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(ctx.Now, 1, 14).FirstOrDefault();
            if (placeholderExpiry == default) placeholderExpiry = ctx.Now.Date.AddDays(7);
            var placeholders = missingTickers.Select(t => MatchKeys.OccSymbol(t, placeholderExpiry, 1m, "C")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var boot = await _quotes.GetQuotesAsync(ctx.Now, placeholders, tickerSet, cancellation);
            foreach (var (k, v) in boot.Underlyings) bootstrapSpots[k] = v;
            foreach (var (k, v) in boot.Options) bootstrapOptions[k] = v;
        }

        // Phase A: enumerate across all tickers.
        var allSkeletons = new List<CandidateSkeleton>();
        foreach (var ticker in _config.Tickers)
        {
            if (!bootstrapSpots.TryGetValue(ticker, out var spot) || spot <= 0m) continue;
            allSkeletons.AddRange(CandidateEnumerator.Enumerate(ticker, spot, ctx.Now, cfg));
        }
        if (allSkeletons.Count == 0) return Array.Empty<OpenProposal>();

        // Phase B (phase-3 quote fetch): pull any symbols not already in ctx.Quotes or the bootstrap result.
        var neededSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skel in allSkeletons)
            foreach (var leg in skel.Legs)
                if (!ctx.Quotes.ContainsKey(leg.Symbol) && !bootstrapOptions.ContainsKey(leg.Symbol)) neededSymbols.Add(leg.Symbol);

        // Keep ctx.Quotes as the primary lookup; overlay fetched symbols only for the new ones.
        // Do NOT copy ctx.Quotes by enumeration — callers may supply non-enumerable fakes (tests) or large live dictionaries.
        IReadOnlyDictionary<string, OptionContractQuote> mergedQuotes = bootstrapOptions.Count > 0
            ? new OverlayQuoteDictionary(ctx.Quotes, bootstrapOptions)
            : ctx.Quotes;
        if (neededSymbols.Count > 0)
        {
            var extra = await _quotes.GetQuotesAsync(ctx.Now, neededSymbols, tickerSet, cancellation);
            if (extra.Options.Count > 0)
            {
                var overlay = new Dictionary<string, OptionContractQuote>(extra.Options, StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in bootstrapOptions) overlay.TryAdd(k, v);
                mergedQuotes = new OverlayQuoteDictionary(ctx.Quotes, overlay);
            }
        }

        // Phase C: score per ticker.
        var reserve = CashReserveHelper.ComputeReserve(_config.CashReserve.Mode, _config.CashReserve.Value, ctx.AccountValue);
        var freeCash = Math.Max(0m, ctx.AccountCash - reserve);

        foreach (var tickerGroup in allSkeletons.GroupBy(s => s.Ticker))
        {
            if (!bootstrapSpots.TryGetValue(tickerGroup.Key, out var spot) || spot <= 0m) continue;
            ctx.TechnicalSignals.TryGetValue(tickerGroup.Key, out var biasSignal);
            var bias = biasSignal?.Score ?? 0m;

            var scoredByStructure = new Dictionary<OpenStructureKind, List<OpenProposal>>();

            foreach (var skel in tickerGroup)
            {
                var p = CandidateScorer.Score(skel, spot, ctx.Now, mergedQuotes, bias, cfg);
                if (p == null) continue;
                if (!scoredByStructure.TryGetValue(p.StructureKind, out var list))
                    scoredByStructure[p.StructureKind] = list = new List<OpenProposal>();
                list.Add(p);
            }

            // Per-structure top-N truncation.
            var survivors = new List<OpenProposal>();
            foreach (var list in scoredByStructure.Values)
                survivors.AddRange(list.OrderByDescending(p => p.BiasAdjustedScore).Take(cfg.MaxCandidatesPerStructurePerTicker));

            // Apply cash sizing.
            for (int i = 0; i < survivors.Count; i++)
                survivors[i] = ApplyCashSizing(survivors[i], freeCash, cfg, bias);

            // Per-ticker top-N.
            output.AddRange(survivors.OrderByDescending(p => p.BiasAdjustedScore).Take(cfg.TopNPerTicker));
        }

        return output;
    }

    private static OpenProposal ApplyCashSizing(OpenProposal p, decimal freeCash, OpenerConfig cfg, decimal bias)
    {
        if (p.CapitalAtRiskPerContract <= 0m)
            return p with { Rationale = CandidateScorer.BuildRationale(p, bias, cfg) };

        var rawMax = Math.Floor(freeCash / p.CapitalAtRiskPerContract);
        var maxQty = rawMax >= cfg.MaxQtyPerProposal ? cfg.MaxQtyPerProposal : (int)rawMax;
        OpenProposal updated;
        if (maxQty >= 1)
        {
            var qty = Math.Min(maxQty, cfg.MaxQtyPerProposal);
            updated = p with { Qty = qty, Legs = ScaleLegs(p.Legs, qty) };
        }
        else
        {
            updated = p with
            {
                Qty = 0,
                CashReserveBlocked = true,
                CashReserveDetail = $"free ${freeCash:F0}, requires ${p.CapitalAtRiskPerContract:F0} per contract"
            };
        }
        return updated with
        {
            Rationale = CandidateScorer.BuildRationale(updated, bias, cfg),
            Fingerprint = CandidateScorer.ComputeFingerprint(updated.Ticker, updated.StructureKind, updated.Legs, updated.Qty)
        };
    }

    private static IReadOnlyList<ProposalLeg> ScaleLegs(IReadOnlyList<ProposalLeg> legs, int qty) =>
        legs.Select(l => l with { Qty = qty }).ToList();
}

/// <summary>Read-only dictionary that layers a small overlay (phase-3 fetched quotes) over a base dictionary (ctx.Quotes).
/// Overlay wins on key collisions. Enumeration reflects only what's in the overlay, but TryGetValue checks both.</summary>
internal sealed class OverlayQuoteDictionary : IReadOnlyDictionary<string, OptionContractQuote>
{
    private readonly IReadOnlyDictionary<string, OptionContractQuote> _base;
    private readonly IReadOnlyDictionary<string, OptionContractQuote> _overlay;

    public OverlayQuoteDictionary(IReadOnlyDictionary<string, OptionContractQuote> baseDict, IReadOnlyDictionary<string, OptionContractQuote> overlay)
    {
        _base = baseDict;
        _overlay = overlay;
    }

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out OptionContractQuote value)
    {
        if (_overlay.TryGetValue(key, out value)) return true;
        return _base.TryGetValue(key, out value);
    }
    public bool ContainsKey(string key) => _overlay.ContainsKey(key) || _base.ContainsKey(key);
    public OptionContractQuote this[string key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys => _overlay.Keys.Concat(_base.Keys.Where(k => !_overlay.ContainsKey(k)));
    public IEnumerable<OptionContractQuote> Values => Keys.Select(k => this[k]);
    public int Count => _overlay.Count + _base.Keys.Count(k => !_overlay.ContainsKey(k));
    public IEnumerator<KeyValuePair<string, OptionContractQuote>> GetEnumerator() => Keys.Select(k => new KeyValuePair<string, OptionContractQuote>(k, this[k])).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
