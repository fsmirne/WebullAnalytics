using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

/// <summary>The single shared rule behind both the backtest book (SimulatedBook.Open) and the live opener
/// (OpenerAutoExecutor.HandleAsync): a candidate can freely ADD to a symbol an existing position already
/// holds on the SAME side (that's just accumulating size — a real account does this all the time), but can
/// never take the OPPOSITE side of a symbol a DIFFERENT position already holds, because the broker/clearer
/// would net that against the held position instead of tracking two independent lots.</summary>
public class HeldLegGuardTests
{
	private static OpenPosition Position(string key, params (string Symbol, Side Side)[] legs) => new(
		Key: key,
		Ticker: "SPY",
		StrategyKind: "ShortPutVertical",
		Legs: legs.Select(l => new PositionLeg(l.Symbol, l.Side, 0m, null, "P", 1)).ToList(),
		InitialNetDebit: -1m,
		AdjustedNetDebit: -1m,
		Quantity: 1);

	private static readonly (string Symbol, Side Side)[] Held590Short580Long =
	{
		("SPY250919P00590000", Side.Sell),
		("SPY250919P00580000", Side.Buy),
	};

	[Fact]
	public void OppositeSideOnSharedLeg_Collides()
	{
		// Held: short 590P / long 580P. Candidate: an unrelated vertical whose LONG leg is 590P — the exact
		// 2025-07-30 SV backtest scenario. The candidate's buy would net against the held short.
		var held = new[] { Position("A", Held590Short580Long) };
		var candidate = new[] { ("SPY250919P00620000", Side.Sell), ("SPY250919P00590000", Side.Buy) };
		Assert.True(HeldLegGuard.CollidesWithHeldLeg(candidate, held));
	}

	[Fact]
	public void SameSideOnSharedLeg_IsNotACollision()
	{
		// Held: short 590P / long 580P. Candidate: a DIFFERENT vertical that also SELLS 590P (same side) —
		// just accumulating more short exposure on that contract, exactly like a real account would. Must
		// be allowed: this was the over-broad case a prior version of the guard wrongly blocked.
		var held = new[] { Position("A", Held590Short580Long) };
		var candidate = new[] { ("SPY250919P00590000", Side.Sell), ("SPY250919P00560000", Side.Buy) };
		Assert.False(HeldLegGuard.CollidesWithHeldLeg(candidate, held));
	}

	[Fact]
	public void ExactReTakeOfTheSameStructure_IsNotACollision()
	{
		// Adding to (or re-taking) the SAME held structure is a normal add, not a foreign collision — that
		// policy belongs to the caller (allowAddToHeldPosition), not this guard.
		var held = new[] { Position("A", Held590Short580Long) };
		Assert.False(HeldLegGuard.CollidesWithHeldLeg(Held590Short580Long, held));
	}

	[Fact]
	public void NoOverlap_IsNotACollision()
	{
		var held = new[] { Position("A", Held590Short580Long) };
		var candidate = new[] { ("SPY250919P00623000", Side.Sell), ("SPY250919P00617000", Side.Buy) };
		Assert.False(HeldLegGuard.CollidesWithHeldLeg(candidate, held));
	}

	[Fact]
	public void SupersetWithMatchingSides_IsNotACollision()
	{
		// Candidate carries every held leg at the SAME side, plus a brand-new leg — no side ever disagrees,
		// so this is just more size, not a collision.
		var held = new[] { Position("A", Held590Short580Long) };
		var candidate = new[] { ("SPY250919P00590000", Side.Sell), ("SPY250919P00580000", Side.Buy), ("SPY250919P00570000", Side.Buy) };
		Assert.False(HeldLegGuard.CollidesWithHeldLeg(candidate, held));
	}

	[Fact]
	public void SupersetWithOneOpposedSide_Collides()
	{
		// Same superset shape, but one shared leg is opposed — that leg alone breaks the held position.
		var held = new[] { Position("A", Held590Short580Long) };
		var candidate = new[] { ("SPY250919P00590000", Side.Buy), ("SPY250919P00580000", Side.Buy), ("SPY250919P00570000", Side.Buy) };
		Assert.True(HeldLegGuard.CollidesWithHeldLeg(candidate, held));
	}
}
