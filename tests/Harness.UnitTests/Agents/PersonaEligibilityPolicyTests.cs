using Harness.Modules.Agents.Application.Accounts;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A criação autônoma resolveu a lacuna de competência e criou um risco novo: uma persona no
/// catálogo PARECE capaz. Delegar a quem não tem ferramenta, perdeu uma capability obrigatória ou
/// está em quarentena é pior do que não ter persona — o card falha depois de gastar cota, e o
/// sistema aparenta capacidade que não tem.
/// </summary>
public sealed class PersonaEligibilityPolicyTests
{
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static PersonaEligibilityInput Persona(
        bool enabled = true,
        bool archived = false,
        string lifecycle = "active",
        string? scope = null,
        string? risk = "high",
        IReadOnlyList<string>? tools = null,
        IReadOnlyList<string>? missing = null) =>
        new(
            "application-security-architect", "specialist", enabled, archived, lifecycle, scope,
            risk, tools ?? ["01ARZ3NDEKTSV4RRFFQ69G5TOO"], missing ?? []);

    [Fact]
    public void AHealthyPersonaTakesTheCard()
    {
        var verdict = PersonaEligibilityPolicy.Evaluate(Persona(), Project, "high", requiresTools: true);

        Assert.True(verdict.Eligible);
        Assert.Equal("persona.eligible", verdict.ReasonCode);
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("disabled")]
    public void APersonaInQuarantineExistsForAuditButNotForWork(string lifecycle)
    {
        // Respeitar o estágio aqui é o que torna a decisão de rebaixamento real, e não decorativa.
        var verdict = PersonaEligibilityPolicy.Evaluate(
            Persona(lifecycle: lifecycle), Project, "low", requiresTools: false);

        Assert.False(verdict.Eligible);
        Assert.Equal("persona.not_executable_lifecycle", verdict.ReasonCode);
    }

    [Fact]
    public void APersonaBornForOneProjectDoesNotCrossIntoAnother()
    {
        var verdict = PersonaEligibilityPolicy.Evaluate(
            Persona(scope: "01ARZ3NDEKTSV4RRFFQ69G5OTH"), Project, "low", requiresTools: false);

        Assert.False(verdict.Eligible);
        Assert.Equal("persona.out_of_project_scope", verdict.ReasonCode);
        // No projeto que a motivou, ela trabalha normalmente.
        Assert.True(PersonaEligibilityPolicy.Evaluate(
            Persona(scope: Project), Project, "low", requiresTools: false).Eligible);
    }

    [Fact]
    public void ARemovedMandatoryCapabilityBlocksDelegation()
    {
        // "Reduzir em vez de recusar" é correto para capability desejável. Para a obrigatória, o
        // fallback silencioso entregaria o card a um agente que já se sabe incapaz.
        var verdict = PersonaEligibilityPolicy.Evaluate(
            Persona(missing: ["repo.write"]), Project, "low", requiresTools: false);

        Assert.False(verdict.Eligible);
        Assert.Equal("persona.missing_required_capability", verdict.ReasonCode);
    }

    [Fact]
    public void APersonaWithoutToolsMayExistButNotExecuteToolWork()
    {
        var noTools = Persona(tools: []);

        Assert.False(
            PersonaEligibilityPolicy.Evaluate(noTools, Project, "low", requiresTools: true).Eligible);
        // Sem exigência de ferramenta ela segue elegível: existir no catálogo continua valendo.
        Assert.True(
            PersonaEligibilityPolicy.Evaluate(noTools, Project, "low", requiresTools: false).Eligible);
    }

    [Fact]
    public void TheCardRiskNeverExceedsWhatThePersonaWasAuthorizedFor()
    {
        var lowRisk = Persona(risk: "low");

        Assert.True(PersonaEligibilityPolicy.Evaluate(lowRisk, Project, "low", requiresTools: false).Eligible);
        var verdict = PersonaEligibilityPolicy.Evaluate(lowRisk, Project, "critical", requiresTools: false);
        Assert.False(verdict.Eligible);
        Assert.Equal("persona.risk_tier_exceeded", verdict.ReasonCode);
    }

    [Fact]
    public void AnUndeclaredRiskTierNeverInheritsAuthorityForCriticalWork()
    {
        var verdict = PersonaEligibilityPolicy.Evaluate(
            Persona(risk: null), Project, "high", requiresTools: false);

        Assert.False(verdict.Eligible);
        Assert.Equal("persona.risk_tier_exceeded", verdict.ReasonCode);
    }

    [Fact]
    public void ADisabledOrArchivedPersonaIsNeverEligible()
    {
        Assert.False(
            PersonaEligibilityPolicy.Evaluate(Persona(enabled: false), Project, "low", requiresTools: false).Eligible);
        Assert.False(
            PersonaEligibilityPolicy.Evaluate(Persona(archived: true), Project, "low", requiresTools: false).Eligible);
    }
}
