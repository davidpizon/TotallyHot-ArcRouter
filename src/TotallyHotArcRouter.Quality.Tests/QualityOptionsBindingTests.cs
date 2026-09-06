using Microsoft.Extensions.Configuration;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Confirms <see cref="DimensionWeightOptions.ExtraWeights"/> - an <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// init property - actually binds from configuration the way the three named axes do (Phase Q3 relies on
/// this to wire CodeJudge/ICE-Score/RACE weights from <c>appsettings.json</c> without touching
/// <see cref="QualityScorer"/> again). The .NET config binder populates a dictionary-typed property by its
/// runtime type, not its declared type, so this is worth pinning explicitly rather than assuming.
/// </summary>
public sealed class QualityOptionsBindingTests
{
    [Fact]
    public void Bind_ExtraWeightsSection_PopulatesTheDictionary()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new("Quality:DimensionWeights:code_generation:Syntax", "0.3"),
                new("Quality:DimensionWeights:code_generation:Analysis", "0.2"),
                new("Quality:DimensionWeights:code_generation:Judge", "0.3"),
                new("Quality:DimensionWeights:code_generation:ExtraWeights:codejudge", "0.1"),
                new("Quality:DimensionWeights:code_generation:ExtraWeights:icescore", "0.05")
            ])
            .Build();

        var options = new QualityOptions();
        configuration.GetSection(QualityOptions.SectionName).Bind(options);

        var weights = options.ResolveWeights("code_generation");
        Assert.Equal(0.3, weights.Syntax);
        Assert.Equal(0.1, weights.ResolveExtraWeight("codejudge"));
        Assert.Equal(0.05, weights.ResolveExtraWeight("icescore"));
        Assert.Equal(0.0, weights.ResolveExtraWeight("race"));
    }
}
