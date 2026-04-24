using Xunit;
using WebullAnalytics.AI;

namespace WebullAnalytics.Tests.AI.Open;

public class OpenerExpiryHelpersTests
{
    [Fact]
    public void ThirdFridayInApril2026Is2026_04_17()
    {
        Assert.Equal(new DateTime(2026, 4, 17), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 4));
    }

    [Fact]
    public void ThirdFridayInMay2026Is2026_05_15()
    {
        Assert.Equal(new DateTime(2026, 5, 15), OpenerExpiryHelpers.ThirdFridayInMonth(2026, 5));
    }

    [Fact]
    public void NextWeeklyExpiriesInRangeReturnsFridaysOnly()
    {
        var asOf = new DateTime(2026, 4, 20); // Monday
        var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
        Assert.All(result, d => Assert.Equal(DayOfWeek.Friday, d.DayOfWeek));
        Assert.All(result, d => Assert.InRange((d - asOf.Date).Days, 3, 10));
    }

    [Fact]
    public void NextWeeklyExpiriesInRangeFromMondayIncludesFriday()
    {
        var asOf = new DateTime(2026, 4, 20); // Monday; Friday = 2026-04-24, DTE = 4
        var result = OpenerExpiryHelpers.NextWeeklyExpiriesInRange(asOf, minDte: 3, maxDte: 10).ToList();
        Assert.Contains(new DateTime(2026, 4, 24), result);
    }

    [Fact]
    public void MonthlyExpiriesInRangeReturnsThirdFridays()
    {
        var asOf = new DateTime(2026, 4, 1);
        var result = OpenerExpiryHelpers.MonthlyExpiriesInRange(asOf, minDte: 0, maxDte: 60).ToList();
        Assert.Contains(new DateTime(2026, 4, 17), result);
        Assert.Contains(new DateTime(2026, 5, 15), result);
    }
}
