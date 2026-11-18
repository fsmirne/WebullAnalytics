using WebullAnalytics.Analyze;
using Xunit;

namespace WebullAnalytics.Tests.Analyze;

// A cascade trades today's expiration. A vendor chain that came back without it (Schwab, 2026-11-18 10:30:59: 4,092 contracts, none expiring that day)
// must fail the read, so the next read retries instead of the setup recording a final "no trade" off a book missing its front expiry.
public class CascadeLiveReaderTests
{
	private static readonly DateTime Today = new(2026, 11, 18);

	[Fact]
	public void AChainWithAContractExpiringToday_HasOne()
	{
		string[] chain = ["SPXW261118C07550000", "SPXW261119C07550000", "SPXW261218P07300000"];
		Assert.True(CascadeLiveReader.HasContractExpiring(chain, "SPXW", Today));
	}

	[Fact]
	public void AChainOfLaterExpirationsOnly_HasNone()
	{
		string[] chain = ["SPXW261119C07550000", "SPXW261120P07300000", "SPXW261218P07300000"];
		Assert.False(CascadeLiveReader.HasContractExpiring(chain, "SPXW", Today));
	}

	[Fact]
	public void AnotherRootsContractExpiringTodayDoesNotCount()
	{
		string[] chain = ["SPY261118C00731000", "SPXW261119C07550000"];
		Assert.False(CascadeLiveReader.HasContractExpiring(chain, "SPXW", Today));
	}
}
