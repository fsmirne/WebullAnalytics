using WebullAnalytics.Utils;
using Xunit;

namespace WebullAnalytics.Tests.Utils;

public class GridWidthEstimatorTests
{
	[Fact]
	public void ComputeMaxGridColumns_ShowLegs_ScalesWithLegCount()
	{
		const int width = 200;
		var cols2 = TableBuilder.ComputeMaxGridColumns(width, displayMode: "pnl", showLegs: true, maxLegCount: 2);
		var cols4 = TableBuilder.ComputeMaxGridColumns(width, displayMode: "pnl", showLegs: true, maxLegCount: 4);
		Assert.True(cols4 < cols2);
	}

	[Fact]
	public void ComputeMaxGridColumns_NoLegs_IgnoresLegCount()
	{
		const int width = 200;
		var cols = TableBuilder.ComputeMaxGridColumns(width, displayMode: "pnl", showLegs: false, maxLegCount: 10);
		var colsDefault = TableBuilder.ComputeMaxGridColumns(width, displayMode: "pnl", showLegs: false);
		Assert.Equal(colsDefault, cols);
	}
}
