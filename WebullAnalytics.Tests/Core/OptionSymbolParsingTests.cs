using System.Globalization;
using System.Text.RegularExpressions;
using WebullAnalytics;
using Xunit;

namespace WebullAnalytics.Tests.Core;

/// <summary>ParseOptionSymbol is hand-rolled for speed (the regex version was a backtest hotspot); it must accept and
/// reject exactly what the original regex + TryParseExact parse did, kept here as the reference.</summary>
public class OptionSymbolParsingTests
{
	private static readonly Regex Reference = new(@"^([A-Z]+)(\d{6})([CP])(\d{8})$");

	private static OptionParsed? ReferenceParse(string symbol)
	{
		var match = Reference.Match(symbol.Trim().ToUpperInvariant());
		if (!match.Success) return null;
		if (!DateTime.TryParseExact(match.Groups[2].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) return null;
		if (!decimal.TryParse(match.Groups[4].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var strikeRaw)) return null;
		return new OptionParsed(match.Groups[1].Value, expiry, match.Groups[3].Value, strikeRaw / 1000m);
	}

	private static void AssertSame(string symbol)
	{
		var expected = ReferenceParse(symbol);
		var actual = ParsingHelpers.ParseOptionSymbol(symbol);
		Assert.Equal(expected, actual);
		if (expected != null) Assert.Equal(expected.Strike.Scale, actual!.Strike.Scale);
	}

	[Theory]
	[InlineData("GME260213C00025000")]
	[InlineData("SPY261019P00776000")]
	[InlineData("SPXW261016C07500500")]
	[InlineData(" spy261019p00776000 \n")]
	[InlineData("SPY991231C00000001")]
	[InlineData("SPY000101P99999999")]
	[InlineData("SPY490101C00100000")]
	[InlineData("SPY500101C00100000")]
	[InlineData("SPY260229C00100000")]
	[InlineData("SPY280229C00100000")]
	[InlineData("SPY261301C00100000")]
	[InlineData("SPY261000C00100000")]
	[InlineData("SPY261032C00100000")]
	[InlineData("SPY261019X00100000")]
	[InlineData("261019C00100000")]
	[InlineData("SPY1261019C00100000")]
	[InlineData("SPY261019C0010000")]
	[InlineData("SPY261019C001000000")]
	[InlineData("SPY26101AC00100000")]
	[InlineData("SPY261019C0010000A")]
	[InlineData("SP Y261019C00100000")]
	[InlineData("SPY")]
	[InlineData("")]
	[InlineData("SPY261019C00100000\n\n")]
	[InlineData("SPY٢٦١٠١٩C00100000")]
	[InlineData("ſPY261019C00100000")]
	[InlineData("ıBM261019C00100000")]
	public void MatchesTheRegexReference(string symbol) => AssertSame(symbol);

	[Fact]
	public void MatchesTheRegexReferenceOnRandomSymbols()
	{
		var rng = new Random(20261015);
		const string alphabet = "ABCPZacp0123456789 -.";
		for (var n = 0; n < 20000; n++)
		{
			var root = new string(Enumerable.Range(0, rng.Next(0, 6)).Select(_ => (char)('A' + rng.Next(26))).ToArray());
			var date = $"{rng.Next(0, 100):00}{rng.Next(0, 14):00}{rng.Next(0, 33):00}";
			var tail = $"{(rng.Next(2) == 0 ? 'C' : 'P')}{rng.Next(0, 100000000):00000000}";
			var symbol = (root + date + tail).ToCharArray();
			if (rng.Next(4) == 0) symbol[rng.Next(symbol.Length)] = alphabet[rng.Next(alphabet.Length)];
			AssertSame(new string(symbol));
		}
	}
}
