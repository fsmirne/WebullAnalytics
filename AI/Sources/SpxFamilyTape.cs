namespace WebullAnalytics.AI.Sources;

/// <summary>Builds an SPX-family (SPX/SPXW/XSP) minute tape: the cash index's RTH bars plus SPY's pre/post-market
/// bars scaled into index units, since the index has no extended-hours session. The one place this splice happens —
/// the live capture, the `wa ai history` backfill and the sniffed-SPX import all route through <see cref="Merge"/>.
///
/// <para>Every bar it emits carries volume 0. An index has no volume: Webull's SPX feed leaves it 0 except for a
/// few stray bars per day whose "volume" is the price (2026-10-19: 7700 at 13:22, 7703 at 13:28), and the scaled
/// SPY bars would otherwise carry SPY's share volume into an SPX file. Both are wrong data for the index, and any
/// volume-weighted consumer read them as real (see <see cref="SessionVwap"/>).</para></summary>
internal static class SpxFamilyTape
{
	/// <summary>SPX bars win on any minute both series cover; SPY bars fill the rest, scaled by the most recent
	/// SPX/SPY close ratio in their overlap. Empty when <paramref name="spx"/> is empty (pure SPY values have no
	/// anchor to scale by); SPX-only when SPY is empty or never overlaps. Oldest-first.</summary>
	internal static List<MinuteBar> Merge(IReadOnlyList<MinuteBar> spx, IReadOnlyList<MinuteBar> spy)
	{
		if (spx.Count == 0) return new List<MinuteBar>();

		var merged = new List<MinuteBar>(spx.Count + spy.Count);
		foreach (var b in spx) merged.Add(b with { Volume = 0 });

		var ratio = TryDeriveRatioFromOverlap(spx, spy);
		if (ratio is > 0m)
		{
			var spxTimestamps = new HashSet<long>(spx.Count);
			foreach (var b in spx) spxTimestamps.Add(b.Timestamp.ToUnixTimeSeconds());
			foreach (var b in spy)
			{
				if (spxTimestamps.Contains(b.Timestamp.ToUnixTimeSeconds())) continue;
				// Round to 2 decimals to match SPX's native precision; unrounded decimal products carry 28 digits.
				merged.Add(new MinuteBar(b.Timestamp, Math.Round(b.Open * ratio.Value, 2), Math.Round(b.High * ratio.Value, 2), Math.Round(b.Low * ratio.Value, 2), Math.Round(b.Close * ratio.Value, 2), 0));
			}
		}

		merged.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
		return merged;
	}

	/// <summary>Returns SPX/SPY ratio computed from the most-recent bar that exists in both series at
	/// the same minute timestamp. Walks SPX bars from newest backward, looking for a matching SPY
	/// timestamp; falls back to null when no overlap exists (e.g., one series is empty or both are
	/// in different sessions). Using the most recent overlap rather than the oldest keeps the ratio
	/// closest to current basis.</summary>
	private static decimal? TryDeriveRatioFromOverlap(IReadOnlyList<MinuteBar> spxBars, IReadOnlyList<MinuteBar> spyBars)
	{
		if (spxBars.Count == 0 || spyBars.Count == 0) return null;

		var spyByTimestamp = new Dictionary<long, decimal>(spyBars.Count);
		foreach (var b in spyBars)
		{
			if (b.Close <= 0m) continue;
			spyByTimestamp[b.Timestamp.ToUnixTimeSeconds()] = b.Close;
		}
		if (spyByTimestamp.Count == 0) return null;

		for (int i = spxBars.Count - 1; i >= 0; i--)
		{
			var ts = spxBars[i].Timestamp.ToUnixTimeSeconds();
			if (spxBars[i].Close <= 0m) continue;
			if (spyByTimestamp.TryGetValue(ts, out var spyClose) && spyClose > 0m)
				return spxBars[i].Close / spyClose;
		}
		return null;
	}
}
