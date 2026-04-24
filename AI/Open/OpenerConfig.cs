using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class OpenerConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("topNPerTicker")] public int TopNPerTicker { get; set; } = 5;
    [JsonPropertyName("maxCandidatesPerStructurePerTicker")] public int MaxCandidatesPerStructurePerTicker { get; set; } = 8;
    [JsonPropertyName("maxQtyPerProposal")] public int MaxQtyPerProposal { get; set; } = 10;
    [JsonPropertyName("directionalFitWeight")] public decimal DirectionalFitWeight { get; set; } = 0.5m;
    [JsonPropertyName("profitBandPct")] public decimal ProfitBandPct { get; set; } = 5.0m;
    [JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 40m;
    [JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;

    /// <summary>Half-width of the EV scenario grid, in standard deviations. Grid points are placed at
    /// ±sigma and ±sigma/2 around spot. Default 1.0 gives a ±1σ / ±0.5σ grid that better matches
    /// realized moves on high-IV names and doesn't overweight fat tails. Prior behavior (and stress tests)
    /// used 2.0 which pumps long-call EV at the expense of pin/theta structures.</summary>
    [JsonPropertyName("scenarioGridSigma")] public decimal ScenarioGridSigma { get; set; } = 1.0m;

    /// <summary>Per-structure multiplier applied to BiasAdjustedScore before ranking. Defaults below
    /// reflect historical edge on the Calendar/Diagonal pair and de-emphasize short verticals and
    /// purely directional long calls/puts. Keyed by OpenStructureKind.ToString(). Override in
    /// ai-config.json under opener.structureWeight.</summary>
    [JsonPropertyName("structureWeight")] public Dictionary<string, decimal> StructureWeight { get; set; } = new(StringComparer.Ordinal)
    {
        ["LongCalendar"] = 1.3m,
        ["LongDiagonal"] = 1.2m,
        ["ShortPutVertical"] = 0.7m,
        ["ShortCallVertical"] = 0.7m,
        ["LongCall"] = 0.3m,
        ["LongPut"] = 0.3m,
    };

    [JsonPropertyName("structures")] public OpenerStructuresConfig Structures { get; set; } = new();
}

internal sealed class OpenerStructuresConfig
{
    [JsonPropertyName("longCalendar")] public OpenerCalendarLikeConfig LongCalendar { get; set; } = new();
    [JsonPropertyName("longDiagonal")] public OpenerCalendarLikeConfig LongDiagonal { get; set; } = new();
    [JsonPropertyName("shortVertical")] public OpenerShortVerticalConfig ShortVertical { get; set; } = new();
    [JsonPropertyName("longCallPut")] public OpenerLongCallPutConfig LongCallPut { get; set; } = new();
}

internal sealed class OpenerCalendarLikeConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
    [JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
    [JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
    [JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
}

internal sealed class OpenerShortVerticalConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
    [JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
    [JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2 };
    [JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
    [JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.30m;
}

internal sealed class OpenerLongCallPutConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("dteMin")] public int DteMin { get; set; } = 21;
    [JsonPropertyName("dteMax")] public int DteMax { get; set; } = 60;
    [JsonPropertyName("deltaMin")] public decimal DeltaMin { get; set; } = 0.30m;
    [JsonPropertyName("deltaMax")] public decimal DeltaMax { get; set; } = 0.60m;
}
