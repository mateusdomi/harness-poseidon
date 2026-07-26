using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// Verifica a projeção opcional das personas sem exigir espelhos documentais no
/// repositório. A fonte factual das personas é o catálogo persistido.
/// </summary>
public sealed class CanonicalAgentDocsDriftTests
{
    [Fact]
    public void RenderingIsDeterministic()
    {
        var first = CanonicalAgentDocs.RenderAll();
        var second = CanonicalAgentDocs.RenderAll();

        Assert.Equal(first, second);
    }

    [Fact]
    public void RenderingCoversEveryCanonicalPersonaWithUniqueNames()
    {
        var expected = CanonicalAgentDefinitions.All
            .Select(seed => CanonicalAgentDocs.FileName(seed))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = CanonicalAgentDocs.RenderAll()
            .Select(document => document.FileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryMirrorCarriesTheDoNotHandEditHeader()
    {
        foreach (var (_, content) in CanonicalAgentDocs.RenderAll())
        {
            Assert.StartsWith(
                "# GENERATED from CanonicalAgentDefinitions — do not hand-edit; run the generator.",
                content,
                StringComparison.Ordinal);
        }
    }
}
