using Spectre.Console;
using System.Text;

namespace WebullAnalytics.Utils;

/// <summary>Holds console output while a Spectre live-render region (progress bar, status spinner) owns the
/// terminal, then replays it verbatim once the region releases. A live region repaints its own line in place,
/// so any other write lands mid-repaint and shreds the bar — a backtest whose store-gap warnings fire from
/// deep inside the day loop printed half-drawn bars with warning text spliced through them.
/// The swap happens at the console boundary (stdout, stderr, and the static <see cref="AnsiConsole"/>) rather
/// than at each writer: nothing below the CLI layer should have to know a bar might be running, and future
/// writers are covered without being taught. Buffered output keeps the real terminal's width and color
/// capabilities, so a replayed line looks exactly as it would have unbuffered.
/// The live region itself must render through <see cref="Live"/> — it is bound to the pre-swap stdout, while
/// the static console now writes into the buffer.</summary>
internal sealed class ConsoleDeferral : IDisposable
{
	private readonly List<(bool IsError, StringBuilder Text)> _segments = [];
	private readonly Lock _gate = new();
	private readonly TextWriter _realOut;
	private readonly TextWriter _realErr;
	private readonly IAnsiConsole _realAnsi;
	private bool _released;

	/// <summary>Console bound to the real terminal — the only thing that may write while the deferral holds.</summary>
	public IAnsiConsole Live => _realAnsi;

	private ConsoleDeferral()
	{
		_realOut = Console.Out;
		_realErr = Console.Error;
		_realAnsi = AnsiConsole.Console;

		Console.SetOut(new SegmentWriter(this, isError: false));
		Console.SetError(new SegmentWriter(this, isError: true));
		var buffered = AnsiConsole.Create(new AnsiConsoleSettings
		{
			Ansi = _realAnsi.Profile.Capabilities.Ansi ? AnsiSupport.Yes : AnsiSupport.No,
			ColorSystem = ToSupport(_realAnsi.Profile.Capabilities.ColorSystem),
			Interactive = InteractionSupport.No,
			Out = new AnsiConsoleOutput(Console.Out),
		});
		// A writer that isn't a terminal reports no width, and Spectre would hard-wrap the buffered markup to
		// its 80-column default — visible only after the replay, when the terminal is much wider.
		buffered.Profile.Width = _realAnsi.Profile.Width;
		buffered.Profile.Height = _realAnsi.Profile.Height;
		AnsiConsole.Console = buffered;
	}

	/// <summary>Starts buffering. Dispose (or let the <c>using</c> scope end, including on an exception) to
	/// restore the console and flush everything that was written meanwhile.</summary>
	public static ConsoleDeferral Begin() => new();

	public void Dispose()
	{
		if (_released) return;
		_released = true;
		Console.SetOut(_realOut);
		Console.SetError(_realErr);
		AnsiConsole.Console = _realAnsi;

		lock (_gate)
		{
			foreach (var (isError, text) in _segments)
				(isError ? _realErr : _realOut).Write(text.ToString());
			_segments.Clear();
		}
	}

	/// <summary>Appends to the tail segment when it belongs to the same stream, so the replay preserves both
	/// the original stdout/stderr split and the order the two were interleaved in.</summary>
	private void Append(bool isError, string text)
	{
		lock (_gate)
		{
			if (_segments.Count > 0 && _segments[^1].IsError == isError) _segments[^1].Text.Append(text);
			else _segments.Add((isError, new StringBuilder(text)));
		}
	}

	private static ColorSystemSupport ToSupport(ColorSystem colors) => colors switch
	{
		ColorSystem.NoColors => ColorSystemSupport.NoColors,
		ColorSystem.Legacy => ColorSystemSupport.Legacy,
		ColorSystem.Standard => ColorSystemSupport.Standard,
		ColorSystem.EightBit => ColorSystemSupport.EightBit,
		ColorSystem.TrueColor => ColorSystemSupport.TrueColor,
		_ => ColorSystemSupport.Detect,
	};

	private sealed class SegmentWriter(ConsoleDeferral owner, bool isError) : TextWriter
	{
		public override Encoding Encoding => Encoding.UTF8;
		public override void Write(char value) => owner.Append(isError, value.ToString());
		public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) owner.Append(isError, value); }
		public override void Write(char[] buffer, int index, int count) => owner.Append(isError, new string(buffer, index, count));
		public override void WriteLine(string? value) => owner.Append(isError, value + NewLine);
	}
}
