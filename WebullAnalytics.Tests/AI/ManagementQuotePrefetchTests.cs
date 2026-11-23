using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

// The symbols FetchQuotesWithHypotheticals pre-fetches for open positions shape the quote snapshot EVERY consumer of a live scan/watch tick reads,
// openers included: whether a second fetch happens, which entries it overwrites, which quotes the parity spot guard solves from. The expected lists
// below were captured from ScenarioEngine.EnumerateHypotheticalSymbols before it moved to ManagementQuotePrefetch (2026-11-22), so any change to
// what is fetched — or in what order — fails here first.
public class ManagementQuotePrefetchTests
{
	private static readonly DateTime AsOf = new(2026, 11, 20);

	private static ManagementQuotePrefetch.LegInfo L(string cp, DateTime exp, decimal k, bool isLong) =>
		new(MatchKeys.OccSymbol("SPY", exp, k, cp), isLong, 1, new OptionParsed("SPY", exp, cp, k));

	private static readonly Dictionary<string, (ManagementQuotePrefetch.LegInfo[] Legs, decimal Spot, decimal StrikeStep, ManagementQuotePrefetch.StructureKind Kind, string Symbols)> Golden = new()
	{
		["single long call"] = ([L("C", new(2026, 12, 18), 700m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.SingleLong, "SPY261127C00700000"),
		["single long expiring before next weekly"] = ([L("C", new(2026, 11, 25), 700m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.SingleLong, ""),
		["single short"] = ([L("P", new(2026, 12, 18), 690m, false)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.SingleShort, ""),
		["call calendar"] = ([L("C", new(2026, 11, 27), 705m, false), L("C", new(2026, 12, 18), 705m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.Calendar, "SPY261204C00705000,SPY261130C00705000,SPY261204C00704000,SPY261204C00706000,SPY261127C00702000,SPY261204C00702000,SPY261218C00702000,SPY261204P00702000,SPY261218P00702000,SPY261127C00703000,SPY261204C00703000,SPY261218C00703000,SPY261204P00703000,SPY261218P00703000"),
		["put diagonal half step"] = ([L("P", new(2026, 11, 24), 695m, false), L("P", new(2026, 12, 11), 690m, true)], 700.0m, 0.5m, ManagementQuotePrefetch.StructureKind.Diagonal, "SPY261127P00695000,SPY261125P00695000,SPY261127P00694500,SPY261127P00695500,SPY261124P00700000,SPY261127P00700000,SPY261211P00700000,SPY261127C00700000,SPY261211C00700000"),
		["diagonal long before new weekly"] = ([L("C", new(2026, 11, 27), 705m, false), L("C", new(2026, 12, 2), 708m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.Diagonal, "SPY261204C00705000,SPY261130C00705000,SPY261204C00704000,SPY261204C00706000,SPY261127C00702000,SPY261204C00702000,SPY261225C00702000,SPY261204P00702000,SPY261225P00702000,SPY261127C00703000,SPY261204C00703000,SPY261225C00703000,SPY261204P00703000,SPY261225P00703000"),
		["short strike at step"] = ([L("C", new(2026, 12, 31), 1m, false), L("C", new(2027, 1, 15), 1m, true)], 1.2m, 1m, ManagementQuotePrefetch.StructureKind.Calendar, "SPY270101C00001000,SPY270101C00002000,SPY270115C00001000,SPY270101P00001000,SPY270115P00001000,SPY261231C00002000,SPY270101C00002000,SPY270115C00002000,SPY270101P00002000,SPY270115P00002000"),
		["vertical"] = ([L("C", new(2026, 12, 18), 700m, true), L("C", new(2026, 12, 18), 710m, false)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.Vertical, ""),
		["iron condor"] = ([L("P", new(2026, 12, 18), 680m, true), L("P", new(2026, 12, 18), 690m, false), L("C", new(2026, 12, 18), 710m, false), L("C", new(2026, 12, 18), 720m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.Unsupported, ""),
		["mixed rights"] = ([L("C", new(2026, 11, 27), 705m, false), L("P", new(2026, 12, 18), 705m, true)], 702.4m, 1m, ManagementQuotePrefetch.StructureKind.Unsupported, ""),
	};

	public static TheoryData<string> Cases() => new(Golden.Keys);

	[Theory]
	[MemberData(nameof(Cases))]
	public void PrefetchedSymbolsAreUnchanged(string name)
	{
		var (legs, spot, strikeStep, expectedKind, expectedSymbols) = Golden[name];
		var kind = ManagementQuotePrefetch.Classify(legs);
		Assert.Equal(expectedKind, kind);
		Assert.Equal(expectedSymbols, string.Join(",", ManagementQuotePrefetch.EnumerateHypotheticalSymbols(legs, kind, spot, strikeStep, AsOf)));
	}
}
