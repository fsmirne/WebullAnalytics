using System.Diagnostics;
using System.Globalization;

namespace WebullAnalytics.Utils;

/// <summary>Times the consecutive phases of one operation: each <see cref="Lap"/> records how long the phase since the previous lap took, so a slow read says where it spent the time.</summary>
internal sealed class PhaseTimer
{
	/// <summary>Phases shorter than this are left out of <see cref="ToString"/>, so the line names what matters.</summary>
	private static readonly TimeSpan ShownFrom = TimeSpan.FromMilliseconds(100);

	private readonly Stopwatch _clock = Stopwatch.StartNew();
	private readonly List<(string Name, TimeSpan Took)> _laps = new();
	private TimeSpan _lastLapAt;

	public TimeSpan Total => _clock.Elapsed;

	/// <summary>Ends the current phase and names it.</summary>
	public void Lap(string name)
	{
		var now = _clock.Elapsed;
		_laps.Add((name, now - _lastLapAt));
		_lastLapAt = now;
	}

	/// <summary>The phases that took at least 100 ms, longest first, e.g. <c>bars 12.4s · chain 3.1s</c>.</summary>
	public override string ToString() => string.Join(" · ", _laps.Where(l => l.Took >= ShownFrom).OrderByDescending(l => l.Took).Select(l => $"{l.Name} {l.Took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s"));
}
