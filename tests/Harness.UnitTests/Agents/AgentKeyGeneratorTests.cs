using Harness.Persistence.Abstractions.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// P1/auto-key: a chave de uma definição de agente é derivada do nome de forma
/// DETERMINÍSTICA e versionada, sempre dentro do alfabeto validado pelo store (minúsculas,
/// dígitos, hífen, ≤100). Nome sem caractere aproveitável cai num fallback estável e a
/// colisão é resolvida por sufixo numérico crescente.
/// </summary>
public sealed class AgentKeyGeneratorTests
{
    [Theory]
    [InlineData("Frontend Specialist", "frontend-specialist")]
    [InlineData("  Chief   Orchestrator  ", "chief-orchestrator")]
    [InlineData("Backend/Infra Reviewer", "backend-infra-reviewer")]
    [InlineData("GLM 4.6 General", "glm-4-6-general")]
    [InlineData("já-existe!!!", "ja-existe")]
    [InlineData("Análise de Solicitação", "analise-de-solicitacao")]
    public void DeriveProducesAValidSlug(string name, string expected)
    {
        var slug = AgentKeyGenerator.Derive(name);
        Assert.Equal(expected, slug);
        Assert.All(slug, character =>
            Assert.True(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ??? ---")]
    [InlineData("你好")]
    public void ANameWithoutUsableCharactersFallsBackToAStableSlug(string? name)
    {
        Assert.Equal(AgentKeyGenerator.Fallback, AgentKeyGenerator.Derive(name));
    }

    [Fact]
    public void TheSlugIsCappedAtTheMaximumLengthWithoutTrailingHyphen()
    {
        var slug = AgentKeyGenerator.Derive(new string('a', 250));
        Assert.Equal(AgentKeyGenerator.MaxLength, slug.Length);
        Assert.DoesNotContain("--", slug, StringComparison.Ordinal);
        Assert.False(slug.EndsWith('-'));
    }

    [Fact]
    public void GenerateReturnsTheBaseSlugWhenItIsFree()
    {
        Assert.Equal("frontend-specialist",
            AgentKeyGenerator.Generate("Frontend Specialist", ["chief-orchestrator"]));
    }

    [Fact]
    public void GenerateAppendsAnIncreasingVersionSuffixOnCollision()
    {
        var existing = new[] { "reviewer", "reviewer-2", "reviewer-3" };
        Assert.Equal("reviewer-4", AgentKeyGenerator.Generate("Reviewer", existing));
    }

    [Fact]
    public void CollisionDetectionIsCaseInsensitive()
    {
        // O store valida em minúsculas, mas a comparação nunca deixa passar um duplicado só
        // por diferença de caixa.
        Assert.Equal("reviewer-2", AgentKeyGenerator.Generate("Reviewer", ["REVIEWER"]));
    }

    [Fact]
    public void TheVersionedSuffixNeverExceedsTheMaximumLength()
    {
        var baseName = new string('a', AgentKeyGenerator.MaxLength);
        var occupied = AgentKeyGenerator.Derive(baseName);
        var key = AgentKeyGenerator.Generate(baseName, [occupied]);

        Assert.True(key.Length <= AgentKeyGenerator.MaxLength);
        Assert.EndsWith("-2", key);
        Assert.NotEqual(occupied, key);
    }

    [Fact]
    public void GenerationIsDeterministicForTheSameInputs()
    {
        var existing = new[] { "agent", "agent-2" };
        Assert.Equal(
            AgentKeyGenerator.Generate("Agent", existing),
            AgentKeyGenerator.Generate("Agent", existing));
    }
}
