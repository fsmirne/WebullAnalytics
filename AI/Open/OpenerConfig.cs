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
	[JsonPropertyName("strikeSteps")] public Dictionary<string, decimal> StrikeSteps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	[JsonPropertyName("volatilityLookbackDays")] public int VolatilityLookbackDays { get; set; } = 20;
	[JsonPropertyName("volatilityFitWeight")] public decimal VolatilityFitWeight { get; set; } = 0.50m;
	[JsonPropertyName("maxPainWeight")] public decimal MaxPainWeight { get; set; } = 0m;
	[JsonPropertyName("statArbWeight")] public decimal StatArbWeight { get; set; } = 0.30m;

	/// <summary>Half-width of the EV scenario grid, in standard deviations. Grid points are placed at
	/// ±sigma and ±sigma/2 around spot. Default 1.0 gives a ±1σ / ±0.5σ grid that better matches
	/// realized moves on high-IV names and doesn't overweight fat tails. Prior behavior (and stress tests)
	/// used 2.0 which pumps long-call EV at the expense of pin/theta structures.</summary>
	[JsonPropertyName("scenarioGridSigma")] public decimal ScenarioGridSigma { get; set; } = 1.0m;

	[JsonPropertyName("structures")] public OpenerStructuresConfig Structures { get; set; } = new();

	public decimal StrikeStepFor(string ticker)
	{
		if (!string.IsNullOrWhiteSpace(ticker) && StrikeSteps.TryGetValue(ticker, out var step) && step > 0m)
			return step;

		throw new KeyNotFoundException($"Missing opener strike step for ticker '{ticker}'.");
	}
}

internal sealed class OpenerStructuresConfig
{
	[JsonPropertyName("longCalendar")] public OpenerCalendarLikeConfig LongCalendar { get; set; } = new();
	[JsonPropertyName("doubleCalendar")] public OpenerDoubleCalendarConfig DoubleCalendar { get; set; } = new();
	[JsonPropertyName("longDiagonal")] public OpenerCalendarLikeConfig LongDiagonal { get; set; } = new();
	[JsonPropertyName("doubleDiagonal")] public OpenerDoubleDiagonalConfig DoubleDiagonal { get; set; } = new();
	[JsonPropertyName("ironButterfly")] public OpenerIronButterflyConfig IronButterfly { get; set; } = new();
	[JsonPropertyName("ironCondor")] public OpenerIronCondorConfig IronCondor { get; set; } = new();
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

internal sealed class OpenerDoubleCalendarConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
}

internal sealed class OpenerDoubleDiagonalConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("shortDteMin")] public int ShortDteMin { get; set; } = 3;
	[JsonPropertyName("shortDteMax")] public int ShortDteMax { get; set; } = 10;
	[JsonPropertyName("longDteMin")] public int LongDteMin { get; set; } = 21;
	[JsonPropertyName("longDteMax")] public int LongDteMax { get; set; } = 60;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 2, 4 };
	[JsonPropertyName("longWingSteps")] public List<int> LongWingSteps { get; set; } = new() { 1 };
}

internal sealed class OpenerIronButterflyConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("wingSteps")] public List<int> WingSteps { get; set; } = new() { 1, 2, 3, 4 };
}

internal sealed class OpenerIronCondorConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("dteMin")] public int DteMin { get; set; } = 3;
	[JsonPropertyName("dteMax")] public int DteMax { get; set; } = 10;
	[JsonPropertyName("widthSteps")] public List<int> WidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("bodyWidthSteps")] public List<int> BodyWidthSteps { get; set; } = new() { 1, 2, 3, 4 };
	[JsonPropertyName("shortDeltaMin")] public decimal ShortDeltaMin { get; set; } = 0.15m;
	[JsonPropertyName("shortDeltaMax")] public decimal ShortDeltaMax { get; set; } = 0.35m;
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
