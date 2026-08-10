using WebullAnalytics.Api;
using Xunit;

namespace WebullAnalytics.Tests.Schwab;

public class SchwabAuthClientTests
{
	// Same shape as a real Schwab redirect (dotted base64-ish code ending in %40, session GUID) — not a real code.
	private const string RealisticUrl = "https://127.0.0.1/?code=C0.aaaaBBBBccccDDDD.0000eeeeFFFFgggg_HHHH-1111iiiiJJJJ%40&session=00000000-1111-2222-3333-444444444444";
	private const string RealisticCode = "C0.aaaaBBBBccccDDDD.0000eeeeFFFFgggg_HHHH-1111iiiiJJJJ@";

	[Fact]
	public void ExtractCode_parses_realistic_schwab_redirect()
	{
		Assert.Equal(RealisticCode, SchwabAuthClient.ExtractCode(RealisticUrl));
	}

	[Fact]
	public void ExtractCode_survives_bracketed_paste_escape_sequences()
	{
		// Terminals with bracketed paste wrap pasted text in \e[200~ ... \e[201~; the markers are
		// invisible on screen but reach Console.ReadLine.
		Assert.Equal(RealisticCode, SchwabAuthClient.ExtractCode($"\x1b[200~{RealisticUrl}\x1b[201~"));
	}

	[Fact]
	public void ExtractCode_returns_null_without_code_parameter()
	{
		Assert.Null(SchwabAuthClient.ExtractCode("https://127.0.0.1/?session=xyz"));
	}
}
