using Xunit;

namespace WebullAnalytics.Tests.Smoke;

public class BuildSmokeTests
{
    [Fact]
    public void ProjectReferenceCompiles()
    {
        // Touches an internal type from the main exe to validate InternalsVisibleTo.
        var cfg = new WebullAnalytics.AI.AIConfig();
        Assert.NotNull(cfg);
    }
}
