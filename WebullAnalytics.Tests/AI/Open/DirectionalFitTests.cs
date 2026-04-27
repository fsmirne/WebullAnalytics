using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI.Open;

public class DirectionalFitTests
{
	[Theory]
	[InlineData(OpenStructureKind.LongCall, 1)]
	[InlineData(OpenStructureKind.ShortPutVertical, 1)]
	[InlineData(OpenStructureKind.LongPut, -1)]
	[InlineData(OpenStructureKind.ShortCallVertical, -1)]
	[InlineData(OpenStructureKind.LongCalendar, 0)]
	[InlineData(OpenStructureKind.LongDiagonal, 0)]
	public void FitSignMatchesSpecTable(OpenStructureKind kind, int expected)
	{
		Assert.Equal(expected, DirectionalFit.SignFor(kind));
	}
}
