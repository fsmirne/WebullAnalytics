using Spectre.Console;
using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Utils;

/// <summary>Locks the invariant the backtest progress bar depends on: while a deferral is open NOTHING
/// reaches the terminal, and on release everything written meanwhile replays on its original stream, in
/// order. Writes leaking through mid-run are what shredded the bar in the first place.</summary>
public class ConsoleDeferralTests
{
	[Fact]
	public void BuffersEveryStreamUntilRelease_ThenReplaysInOrder()
	{
		var terminalOut = new StringWriter();
		var terminalErr = new StringWriter();
		var priorOut = Console.Out;
		var priorErr = Console.Error;
		var priorAnsi = AnsiConsole.Console;
		Console.SetOut(terminalOut);
		Console.SetError(terminalErr);

		try
		{
			using (ConsoleDeferral.Begin())
			{
				Console.WriteLine("first");
				Console.Error.WriteLine("problem");
				AnsiConsole.MarkupLine("[green]third[/]");
				Assert.Equal("", terminalOut.ToString());
				Assert.Equal("", terminalErr.ToString());
			}

			var stdout = terminalOut.ToString();
			Assert.Contains("first", stdout);
			Assert.Contains("third", stdout);
			Assert.True(stdout.IndexOf("first", StringComparison.Ordinal) < stdout.IndexOf("third", StringComparison.Ordinal), "stdout writes must replay in the order they were made");
			Assert.DoesNotContain("problem", stdout);
			Assert.Contains("problem", terminalErr.ToString());
		}
		finally
		{
			Console.SetOut(priorOut);
			Console.SetError(priorErr);
			AnsiConsole.Console = priorAnsi;
		}
	}

	[Fact]
	public void LiveConsoleWritesStraightThrough_AndDisposeRestoresTheRealStreams()
	{
		var terminalOut = new StringWriter();
		var priorOut = Console.Out;
		var priorAnsi = AnsiConsole.Console;
		Console.SetOut(terminalOut);
		var terminalConsoleOut = Console.Out;   // SetOut hands back a synchronizing wrapper, not the writer itself
		AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = new AnsiConsoleOutput(terminalOut) });
		var terminalAnsi = AnsiConsole.Console;

		try
		{
			using (var deferral = ConsoleDeferral.Begin())
			{
				// The live region (the progress bar) owns the terminal and must bypass the buffer.
				deferral.Live.WriteLine("bar");
				Assert.Contains("bar", terminalOut.ToString());
			}

			Assert.Same(terminalConsoleOut, Console.Out);
			Assert.Same(terminalAnsi, AnsiConsole.Console);
		}
		finally
		{
			Console.SetOut(priorOut);
			AnsiConsole.Console = priorAnsi;
		}
	}
}
