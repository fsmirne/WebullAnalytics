using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebullAnalytics.AI;

internal sealed class AIConfig
{
	[JsonPropertyName("tickers")] public List<string> Tickers { get; set; } = new();
	[JsonPropertyName("tickIntervalSeconds")] public int TickIntervalSeconds { get; set; } = 60;
	[JsonPropertyName("marketHours")] public MarketHoursConfig MarketHours { get; set; } = new();
	[JsonPropertyName("quoteSource")] public string QuoteSource { get; set; } = "webull";
	[JsonPropertyName("positionSource")] public PositionSourceConfig PositionSource { get; set; } = new();
	[JsonPropertyName("cashReserve")] public CashReserveConfig CashReserve { get; set; } = new();
	[JsonPropertyName("log")] public LogConfig Log { get; set; } = new();
	[JsonPropertyName("rules")] public RulesConfig Rules { get; set; } = new();
}

internal sealed class MarketHoursConfig
{
	[JsonPropertyName("start")] public string Start { get; set; } = "09:30";
	[JsonPropertyName("end")] public string End { get; set; } = "16:00";
	[JsonPropertyName("tz")] public string Tz { get; set; } = "America/New_York";
}

internal sealed class PositionSourceConfig
{
	[JsonPropertyName("type")] public string Type { get; set; } = "openapi";
	[JsonPropertyName("account")] public string Account { get; set; } = "default";
}

internal sealed class CashReserveConfig
{
	[JsonPropertyName("mode")] public string Mode { get; set; } = "percent";   // "percent" or "absolute"
	[JsonPropertyName("value")] public decimal Value { get; set; } = 25m;      // percent of account value, or absolute $
}

internal sealed class LogConfig
{
	[JsonPropertyName("path")] public string Path { get; set; } = "data/ai-proposals.log";
	[JsonPropertyName("consoleVerbosity")] public string ConsoleVerbosity { get; set; } = "normal"; // quiet | normal | debug
}

internal sealed class RulesConfig
{
	[JsonPropertyName("stopLoss")] public StopLossConfig StopLoss { get; set; } = new();
	[JsonPropertyName("opportunisticRoll")] public OpportunisticRollConfig OpportunisticRoll { get; set; } = new();
	[JsonPropertyName("takeProfit")] public TakeProfitConfig TakeProfit { get; set; } = new() { Enabled = false };
	[JsonPropertyName("defensiveRoll")] public DefensiveRollConfig DefensiveRoll { get; set; } = new() { Enabled = false };
	[JsonPropertyName("rollShortOnExpiry")] public RollShortOnExpiryConfig RollShortOnExpiry { get; set; } = new() { Enabled = false };
}

internal sealed class OpportunisticRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Minimum P&L-per-day-per-contract improvement (dollars) vs hold required to fire a proposal.</summary>
	[JsonPropertyName("minImprovementPerDayPerContract")] public decimal MinImprovementPerDayPerContract { get; set; } = 0.50m;
	[JsonPropertyName("ivDefaultPct")] public decimal IvDefaultPct { get; set; } = 40m;
	[JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;
	/// <summary>Minimum OTM distance required for the new short leg, as a percentage of spot, at neutral technicals.</summary>
	[JsonPropertyName("baseOtmBufferPct")] public decimal BaseOtmBufferPct { get; set; } = 2.0m;
	/// <summary>Scales the OTM buffer by (1 + |compositeScore| × multiplier) when technicals are extended.</summary>
	[JsonPropertyName("technicalBufferMultiplier")] public decimal TechnicalBufferMultiplier { get; set; } = 1.5m;
	/// <summary>Maximum allowed increase in net position delta magnitude after the roll, as a percentage of current delta.</summary>
	[JsonPropertyName("maxDeltaIncreasePct")] public decimal MaxDeltaIncreasePct { get; set; } = 25.0m;
	[JsonPropertyName("technicalFilter")] public TechnicalFilterConfig TechnicalFilter { get; set; } = new();
}

internal sealed class StopLossConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("maxDebitMultiplier")] public decimal MaxDebitMultiplier { get; set; } = 1.5m;
	[JsonPropertyName("spotBeyondBreakevenPct")] public decimal SpotBeyondBreakevenPct { get; set; } = 3.0m;
}

internal sealed class TakeProfitConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("pctOfMaxProfit")] public decimal PctOfMaxProfit { get; set; } = 60m;
}

internal sealed class DefensiveRollConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("spotWithinPctOfShortStrike")] public decimal SpotWithinPctOfShortStrike { get; set; } = 1.0m;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 3;
	[JsonPropertyName("strikeStep")] public decimal StrikeStep { get; set; } = 0.50m;
}

internal sealed class RollShortOnExpiryConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	[JsonPropertyName("triggerDTE")] public int TriggerDTE { get; set; } = 2;
	[JsonPropertyName("maxShortPremium")] public decimal MaxShortPremium { get; set; } = 0.10m;
	[JsonPropertyName("minRollCredit")] public decimal MinRollCredit { get; set; } = 0.05m;
}

internal sealed class TechnicalFilterConfig
{
	[JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
	/// <summary>Number of daily closes to fetch. Must be ≥ 20 (required for SMA20).</summary>
	[JsonPropertyName("lookbackDays")] public int LookbackDays { get; set; } = 20;
	[JsonPropertyName("smaWeight")] public decimal SmaWeight { get; set; } = 1.0m;
	[JsonPropertyName("rsiWeight")] public decimal RsiWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumWeight")] public decimal MomentumWeight { get; set; } = 1.0m;
	[JsonPropertyName("momentumDays")] public int MomentumDays { get; set; } = 5;
	/// <summary>Composite score threshold above which call positions are blocked from rolling.</summary>
	[JsonPropertyName("bullishBlockThreshold")] public decimal BullishBlockThreshold { get; set; } = 0.25m;
	/// <summary>Composite score threshold below which put positions are blocked from rolling.</summary>
	[JsonPropertyName("bearishBlockThreshold")] public decimal BearishBlockThreshold { get; set; } = -0.25m;
}

internal static class AIConfigLoader
{
	internal const string ConfigPath = "data/ai-config.json";

	/// <summary>Loads and validates ai-config.json. Returns null (with stderr message) on any failure.</summary>
	internal static AIConfig? Load()
	{
		var path = Program.ResolvePath(ConfigPath);
		if (!File.Exists(path))
		{
			Console.Error.WriteLine($"Error: ai config not found at '{ConfigPath}'.");
			Console.Error.WriteLine($"  Run: cp ai-config.example.json {ConfigPath} and edit.");
			return null;
		}

		AIConfig? config;
		try { config = JsonSerializer.Deserialize<AIConfig>(File.ReadAllText(path)); }
		catch (JsonException ex) { Console.Error.WriteLine($"Error: failed to parse ai-config.json: {ex.Message}"); return null; }

		if (config == null) { Console.Error.WriteLine("Error: ai-config.json is empty."); return null; }

		var err = Validate(config);
		if (err != null) { Console.Error.WriteLine($"Error: ai-config.json: {err}"); return null; }

		return config;
	}

	/// <summary>Returns null when valid; otherwise a human-readable error string naming the field and bound.</summary>
	internal static string? Validate(AIConfig c)
	{
		if (c.Tickers.Count == 0) return "tickers: must contain at least one symbol";
		if (c.TickIntervalSeconds < 1 || c.TickIntervalSeconds > 3600) return $"tickIntervalSeconds: must be in [1, 3600], got {c.TickIntervalSeconds}";
		if (!TimeSpan.TryParseExact(c.MarketHours.Start, "hh\\:mm", CultureInfo.InvariantCulture, out _)) return $"marketHours.start: must be HH:MM, got '{c.MarketHours.Start}'";
		if (!TimeSpan.TryParseExact(c.MarketHours.End, "hh\\:mm", CultureInfo.InvariantCulture, out _)) return $"marketHours.end: must be HH:MM, got '{c.MarketHours.End}'";
		if (c.QuoteSource is not ("webull" or "yahoo")) return $"quoteSource: must be 'webull' or 'yahoo', got '{c.QuoteSource}'";
		if (c.PositionSource.Type is not ("openapi" or "jsonl")) return $"positionSource.type: must be 'openapi' or 'jsonl', got '{c.PositionSource.Type}'";
		if (c.CashReserve.Mode is not ("percent" or "absolute")) return $"cashReserve.mode: must be 'percent' or 'absolute', got '{c.CashReserve.Mode}'";
		if (c.CashReserve.Value < 0m) return $"cashReserve.value: must be non-negative, got {c.CashReserve.Value}";
		if (c.CashReserve.Mode == "percent" && c.CashReserve.Value > 100m) return $"cashReserve.value: must be ≤ 100 for mode 'percent', got {c.CashReserve.Value}";
		if (c.Log.ConsoleVerbosity is not ("quiet" or "normal" or "debug")) return $"log.consoleVerbosity: must be quiet|normal|debug, got '{c.Log.ConsoleVerbosity}'";

		var sl = c.Rules.StopLoss;
		if (sl.MaxDebitMultiplier <= 0m) return $"rules.stopLoss.maxDebitMultiplier: must be > 0, got {sl.MaxDebitMultiplier}";
		if (sl.SpotBeyondBreakevenPct < 0m) return $"rules.stopLoss.spotBeyondBreakevenPct: must be ≥ 0, got {sl.SpotBeyondBreakevenPct}";

		var tp = c.Rules.TakeProfit;
		if (tp.PctOfMaxProfit <= 0m || tp.PctOfMaxProfit > 100m) return $"rules.takeProfit.pctOfMaxProfit: must be in (0, 100], got {tp.PctOfMaxProfit}";

		var dr = c.Rules.DefensiveRoll;
		if (dr.SpotWithinPctOfShortStrike < 0m) return $"rules.defensiveRoll.spotWithinPctOfShortStrike: must be ≥ 0, got {dr.SpotWithinPctOfShortStrike}";
		if (dr.TriggerDTE < 0) return $"rules.defensiveRoll.triggerDTE: must be ≥ 0, got {dr.TriggerDTE}";
		if (dr.StrikeStep <= 0m) return $"rules.defensiveRoll.strikeStep: must be > 0, got {dr.StrikeStep}";

		var rr = c.Rules.RollShortOnExpiry;
		if (rr.TriggerDTE < 0) return $"rules.rollShortOnExpiry.triggerDTE: must be ≥ 0, got {rr.TriggerDTE}";
		if (rr.MaxShortPremium < 0m) return $"rules.rollShortOnExpiry.maxShortPremium: must be ≥ 0, got {rr.MaxShortPremium}";
		if (rr.MinRollCredit < 0m) return $"rules.rollShortOnExpiry.minRollCredit: must be ≥ 0, got {rr.MinRollCredit}";

		var or = c.Rules.OpportunisticRoll;
		if (or.BaseOtmBufferPct < 0m) return $"rules.opportunisticRoll.baseOtmBufferPct: must be ≥ 0, got {or.BaseOtmBufferPct}";
		if (or.TechnicalBufferMultiplier < 0m) return $"rules.opportunisticRoll.technicalBufferMultiplier: must be ≥ 0, got {or.TechnicalBufferMultiplier}";
		if (or.MaxDeltaIncreasePct < 0m) return $"rules.opportunisticRoll.maxDeltaIncreasePct: must be ≥ 0, got {or.MaxDeltaIncreasePct}";

		var tf = or.TechnicalFilter;
		if (tf.Enabled)
		{
			if (tf.LookbackDays < 20) return $"rules.opportunisticRoll.technicalFilter.lookbackDays: must be ≥ 20, got {tf.LookbackDays}";
			if (tf.SmaWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.smaWeight: must be ≥ 0, got {tf.SmaWeight}";
			if (tf.RsiWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.rsiWeight: must be ≥ 0, got {tf.RsiWeight}";
			if (tf.MomentumWeight < 0m) return $"rules.opportunisticRoll.technicalFilter.momentumWeight: must be ≥ 0, got {tf.MomentumWeight}";
			if (tf.MomentumDays < 1) return $"rules.opportunisticRoll.technicalFilter.momentumDays: must be ≥ 1, got {tf.MomentumDays}";
		}

		return null;
	}
}
