namespace WebullAnalytics.Utils;

public static class AssetExtensions
{
	public static string DisplayName(this Asset asset) => asset switch
	{
		Asset.OptionStrategy => "Option Strategy",
		_ => asset.ToString(),
	};
}
