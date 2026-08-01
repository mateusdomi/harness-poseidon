using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A fronteira entre "a chefe administra a própria equipe" (agora permitido por decisão do dono) e
/// "a chefe amplia a própria autoridade" (nunca).
/// </summary>
public sealed class TeamActionPolicyTests
{
    private static ProposedPersona Persona(
        string key = "application-security-architect",
        string? purpose = null,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? risks = null) =>
        new(
            key,
            "Arquiteto de Segurança de Aplicações",
            purpose ?? "Projetar e revisar controles de segurança de aplicações e ameaças do fluxo de login.",
            "architecture-security",
            ["Modelar ameaças", "Revisar controles"],
            ["Não implementa código de produção"],
            capabilities ?? ["repo.read", "docs.write"],
            risks ?? ["medium", "high"]);

    [Fact]
    public void AWellFormedPersonaIsAccepted()
    {
        var verdict = TeamActionPolicy.Evaluate(Persona());

        Assert.True(verdict.Allowed);
        Assert.Equal("team.persona_allowed", verdict.ReasonCode);
        Assert.Equal(["repo.read", "docs.write"], verdict.Persona!.RequiredCapabilities);
    }

    [Fact]
    public void ForbiddenCapabilitiesAreStrippedInsteadOfKillingThePersona()
    {
        // Recusar a persona inteira por uma capability a mais devolveria o trabalho ao
        // generalista — resultado pior. O que foi cortado fica explícito.
        var verdict = TeamActionPolicy.Evaluate(Persona(
            capabilities: ["repo.read", "canon.write", "gate.approve", "secrets.read"]));

        Assert.True(verdict.Allowed);
        Assert.Equal("team.persona_reduced", verdict.ReasonCode);
        Assert.Equal(["repo.read"], verdict.Persona!.RequiredCapabilities);
        Assert.Equal(["canon.write", "gate.approve", "secrets.read"], verdict.RemovedCapabilities);
    }

    [Fact]
    public void APersonaWithOnlyForbiddenAuthorityIsRefused()
    {
        var verdict = TeamActionPolicy.Evaluate(Persona(
            capabilities: ["self.review", "approve.own", "user.publish", "agents.create"]));

        Assert.False(verdict.Allowed);
        Assert.Equal("team.no_safe_capability", verdict.ReasonCode);
        Assert.Equal(4, verdict.RemovedCapabilities.Count);
    }

    [Theory]
    [InlineData("specialty")]
    [InlineData("responsibilities")]
    [InlineData("constraints")]
    [InlineData("capabilities")]
    public void AnOperationallyIncompletePersonaIsRefused(string missing)
    {
        var persona = Persona() with
        {
            Specialty = missing == "specialty" ? string.Empty : "architecture-security",
            Responsibilities = missing == "responsibilities" ? [] : ["Modelar ameaças"],
            Constraints = missing == "constraints" ? [] : ["Não aprova o próprio trabalho"],
            RequiredCapabilities = missing == "capabilities" ? [] : ["repo.read"],
        };

        var verdict = TeamActionPolicy.Evaluate(persona);

        Assert.False(verdict.Allowed);
        Assert.Equal("team.incomplete_persona", verdict.ReasonCode);
    }

    [Fact]
    public void AVaguePurposeIsRefusedBecauseNobodyCouldAuditItLater()
    {
        var verdict = TeamActionPolicy.Evaluate(Persona(purpose: "Ajudar"));
        Assert.False(verdict.Allowed);
        Assert.Equal("team.purpose_too_vague", verdict.ReasonCode);
    }

    [Theory]
    [InlineData("Chave Com Espaço")]
    [InlineData("ab")]
    [InlineData("-comeca-com-hifen")]
    public void AMalformedKeyIsRefused(string key)
    {
        Assert.False(TeamActionPolicy.Evaluate(Persona(key: key)).Allowed);
    }

    [Fact]
    public void AnUppercaseKeyIsNormalizedInsteadOfRefused()
    {
        // A chave é identificador, não conteúdo: normalizar é acolher a variação sem afrouxar o
        // formato — espaço e pontuação continuam recusados.
        var verdict = TeamActionPolicy.Evaluate(Persona(key: "Application-Security-Architect"));
        Assert.True(verdict.Allowed);
        Assert.Equal("application-security-architect", verdict.Persona!.Key);
    }

    [Fact]
    public void WithoutADeclaredRiskTierThePersonaIsBornAtTheLowest()
    {
        // Menor privilégio vale também para o tipo de trabalho que ela pode receber.
        var verdict = TeamActionPolicy.Evaluate(Persona(risks: ["inventado"]));
        Assert.Equal(["low"], verdict.Persona!.RiskTiers);
    }

    [Fact]
    public void ANullProposalIsRefusedInsteadOfDefaulted()
    {
        var verdict = TeamActionPolicy.Evaluate(null);
        Assert.False(verdict.Allowed);
        Assert.Equal("team.persona_missing", verdict.ReasonCode);
    }

    [Fact]
    public void AnExistingPersonaWithTheSameSpecialtyCoversTheProposal()
    {
        Assert.True(TeamActionPolicy.IsCoveredBy(
            TeamActionPolicy.Evaluate(Persona()).Persona!,
            "architecture-security", "architecture-security", "Security Architect"));
    }

    [Fact]
    public void AnUnrelatedPersonaDoesNotCoverTheProposal()
    {
        Assert.False(TeamActionPolicy.IsCoveredBy(
            TeamActionPolicy.Evaluate(Persona()).Persona!,
            "technical-writer", "documentation", "Escreve guias e manuais de uso do produto."));
    }

    [Fact]
    public void TheSameKeyAlwaysCountsAsCoverage()
    {
        Assert.True(TeamActionPolicy.IsCoveredBy(
            TeamActionPolicy.Evaluate(Persona()).Persona!,
            "application-security-architect", null, string.Empty));
    }
}
