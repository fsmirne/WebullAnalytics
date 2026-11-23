using System.Text.Json.Nodes;
using WebullAnalytics.AI;
using Xunit;

namespace WebullAnalytics.Tests.AI;

public class AIConfigMergeTests
{
	[Fact]
	public void DeepMerge_NullBase_ReturnsOverrideClone()
	{
		var overrideNode = JsonNode.Parse("""{"a":1}""");
		var merged = AIConfigMerge.DeepMerge(null, overrideNode);
		Assert.Equal("""{"a":1}""", merged!.ToJsonString());
	}

	[Fact]
	public void DeepMerge_NullOverride_ReturnsBaseClone()
	{
		var baseNode = JsonNode.Parse("""{"a":1}""");
		var merged = AIConfigMerge.DeepMerge(baseNode, null);
		Assert.Equal("""{"a":1}""", merged!.ToJsonString());
	}

	[Fact]
	public void DeepMerge_DisjointKeys_UnionsBoth()
	{
		var b = JsonNode.Parse("""{"a":1}""");
		var o = JsonNode.Parse("""{"b":2}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		Assert.Equal(1, (int)merged["a"]!);
		Assert.Equal(2, (int)merged["b"]!);
	}

	[Fact]
	public void DeepMerge_OverlappingScalar_OverrideWins()
	{
		var b = JsonNode.Parse("""{"a":1}""");
		var o = JsonNode.Parse("""{"a":99}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		Assert.Equal(99, (int)merged["a"]!);
	}

	[Fact]
	public void DeepMerge_NestedObjects_MergeRecursively()
	{
		var b = JsonNode.Parse("""{"opener":{"weight":0.3,"keep":true}}""");
		var o = JsonNode.Parse("""{"opener":{"weight":0.5}}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		var opener = (JsonObject)merged["opener"]!;
		Assert.Equal(0.5, (double)opener["weight"]!);
		Assert.True((bool)opener["keep"]!);
	}

	[Fact]
	public void DeepMerge_ArraysReplaced_NotConcatenated()
	{
		// "Tuning knob arrays" like widthSteps must be replaced by the override, not merged. Merging
		// would silently double-up entries every time a user adds a per-ticker override.
		var b = JsonNode.Parse("""{"steps":[1,2,3]}""");
		var o = JsonNode.Parse("""{"steps":[10]}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		Assert.Equal("""[10]""", merged["steps"]!.ToJsonString());
	}

	[Fact]
	public void DeepMerge_DeeplyNested_PreservesUntouchedBranches()
	{
		var b = JsonNode.Parse("""{"opener":{"structures":{"ironCondor":{"enabled":true,"widthSteps":[1,2]},"longCallPut":{"enabled":true}}}}""");
		var o = JsonNode.Parse("""{"opener":{"structures":{"ironCondor":{"widthSteps":[5]}}}}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		var ic = (JsonObject)merged["opener"]!["structures"]!["ironCondor"]!;
		Assert.True((bool)ic["enabled"]!);
		Assert.Equal("""[5]""", ic["widthSteps"]!.ToJsonString());
		// longCallPut comes from the base and survives untouched.
		var lcp = (JsonObject)merged["opener"]!["structures"]!["longCallPut"]!;
		Assert.True((bool)lcp["enabled"]!);
	}

	[Fact]
	public void DeepMerge_NestedDictionary_MergesByKey()
	{
		// strikeSteps is a {ticker: step} dict — merging should union by key.
		var b = JsonNode.Parse("""{"strikeSteps":{"GME":0.50,"SPY":1.00}}""");
		var o = JsonNode.Parse("""{"strikeSteps":{"SPXW":5.00,"SPY":2.00}}""");
		var merged = (JsonObject)AIConfigMerge.DeepMerge(b, o)!;
		var s = (JsonObject)merged["strikeSteps"]!;
		Assert.Equal(0.50, (double)s["GME"]!);
		Assert.Equal(2.00, (double)s["SPY"]!);   // override wins
		Assert.Equal(5.00, (double)s["SPXW"]!);  // added by override
	}

	[Fact]
	public void LoadMerged_BothFilesMissing_ReturnsNull()
	{
		Assert.Null(AIConfigMerge.LoadMerged(null, null));
		Assert.Null(AIConfigMerge.LoadMerged("/nonexistent/a.json", "/nonexistent/b.json"));
	}

	[Fact]
	public void LoadMerged_ARemovedRuleSectionIsIgnored()
	{
		// rules.opportunisticRoll (OpportunisticRollRule, deleted 2026-11-22) is still in every existing ai-config: it must load and validate,
		// and leave the rules that are still there as configured.
		var path = Path.Combine(Path.GetTempPath(), $"ai-config-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, """{"indicators":{"strikeStep":1},"rules":{"opportunisticRoll":{"enabled":true,"minImprovementPerDayPerContract":0.5},"legInShort":{"enabled":true,"minDTE":0}}}""");
		try
		{
			var config = AIConfigMerge.LoadMerged(path);
			Assert.NotNull(config);
			Assert.Null(AIConfigLoader.Validate(config!));
			Assert.True(config!.Rules.LegInShort.Enabled);
			Assert.Equal(0, config.Rules.LegInShort.MinDTE);
		}
		finally { File.Delete(path); }
	}
}
