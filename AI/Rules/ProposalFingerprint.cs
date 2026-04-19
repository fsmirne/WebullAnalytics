using System.Globalization;

namespace WebullAnalytics.AI.Rules;

/// <summary>
/// Stable identity of a proposal for idempotency dedup. Two proposals with the same fingerprint
/// on consecutive ticks are suppressed from console output (JSONL log always records them).
/// </summary>
internal readonly record struct ProposalFingerprint(string Rule, string PositionKey, string StructuralParams)
{
	/// <summary>Builds a fingerprint from a proposal. StructuralParams are the material fields
	/// (leg symbols, quantities, and NetDebit rounded to 2 decimals) — not the rationale or clock.</summary>
	public static ProposalFingerprint From(ManagementProposal p)
	{
		var legs = string.Join("|", p.Legs.Select(l => $"{l.Action}:{l.Symbol}:{l.Qty}"));
		var net = p.NetDebit.ToString("F2", CultureInfo.InvariantCulture);
		return new ProposalFingerprint(p.Rule, p.PositionKey, $"{legs};{net}");
	}

	/// <summary>Returns true if two fingerprints are materially equivalent (same rule, position, legs, and net within $0.02).</summary>
	public static bool AreEquivalent(ProposalFingerprint a, ProposalFingerprint b) =>
		a.Rule == b.Rule && a.PositionKey == b.PositionKey && a.StructuralParams == b.StructuralParams;
}
