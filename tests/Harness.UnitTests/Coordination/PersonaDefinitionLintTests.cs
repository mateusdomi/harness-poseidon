using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// F17/B8 — persona é procuração. Definições perigosas não parecem perigosas na hora de criar:
/// parecem convenientes. "Dá acesso a tudo para não travar" resolve o atrito de hoje e cria o
/// incidente de amanhã.
/// </summary>
public sealed class PersonaDefinitionLintTests
{
    private static PersonaDefinition Safe() => new(
        "backend-billing",
        "Especialista de cobrança",
        ["read", "edit"],
        ["src/Modules/Billing"],
        ["governance", "coordination"]);

    [Fact]
    public void AWellBoundedPersonaIsAccepted()
    {
        var result = PersonaDefinitionLint.Inspect(Safe());

        Assert.True(result.Accepted);
        Assert.Empty(result.Findings);
    }

    /// <summary>Gate da fase: persona perigosa é recusada COM motivo.</summary>
    [Fact]
    public void ArbitraryExecutionPlusBroadWriteIsRefusedWithAReason()
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with
        {
            Tools = ["bash", "write"],
            AllowedScopes = ["."]
        });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.DangerousToolCombination,
            result.Findings.Select(finding => finding.Code));
        var dangerous = result.Findings.First(f => f.Code == PersonaDefinitionLint.DangerousToolCombination);
        Assert.Contains("acesso irrestrito", dangerous.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("/")]
    [InlineData("*")]
    [InlineData("**")]
    [InlineData("src")]
    [InlineData("**/*")]
    public void ARootLikeScopeDefeatsEveryOtherGuard(string scope)
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with { AllowedScopes = [scope] });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.ScopeTooBroad,
            result.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void MissingDenylistIsRefusedBecauseOmissionIsNotADecision()
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with { DeniedScopes = [] });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.MissingDenylist,
            result.Findings.Select(finding => finding.Code));
    }

    [Theory]
    [InlineData("governance")]
    [InlineData("governance/core.md")]
    [InlineData("coordination")]
    [InlineData(".github/workflows")]
    [InlineData("tools/backend/.tooling")]
    public void ProtectedPathsCanNeverBeGrantedAsAllowedScope(string scope)
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with { AllowedScopes = [scope] });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.ProtectedPathAllowed,
            result.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void APersonaWithoutToolsCouldNotExecuteAnythingAndIsRefused()
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with { Tools = [] });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.NoToolsDeclared,
            result.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void ArbitraryExecutionAloneWithNarrowScopeIsStillRefusedWhenItCanWrite()
    {
        // Executar comando arbitrário + escrever é a combinação, mesmo com escopo estreito:
        // um comando arbitrário não respeita o escopo declarado.
        var result = PersonaDefinitionLint.Inspect(Safe() with { Tools = ["bash", "edit"] });

        Assert.False(result.Accepted);
        Assert.Contains(
            PersonaDefinitionLint.DangerousToolCombination,
            result.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void ReadOnlyExecutionWithNarrowScopeIsAcceptable()
    {
        var result = PersonaDefinitionLint.Inspect(Safe() with { Tools = ["bash", "read"] });

        Assert.True(result.Accepted);
    }

    /// <summary>Default-FAIL: qualquer achado recusa, e vários achados aparecem juntos.</summary>
    [Fact]
    public void EveryProblemIsReportedAtOnceSoTheFixIsASingleConversation()
    {
        var result = PersonaDefinitionLint.Inspect(new PersonaDefinition(
            "perigosa", "Tudo", ["bash", "write"], ["."], []));

        Assert.False(result.Accepted);
        Assert.Contains(PersonaDefinitionLint.ScopeTooBroad, result.Findings.Select(f => f.Code));
        Assert.Contains(PersonaDefinitionLint.DangerousToolCombination, result.Findings.Select(f => f.Code));
        Assert.Contains(PersonaDefinitionLint.MissingDenylist, result.Findings.Select(f => f.Code));
        Assert.All(result.Findings, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Message)));
    }

    [Fact]
    public void AnUnnamedPersonaIsRejectedOutright()
    {
        Assert.Throws<ArgumentException>(() => PersonaDefinitionLint.Inspect(Safe() with { Key = "" }));
    }
}
