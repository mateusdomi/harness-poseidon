using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// B8/F17 — o contrato entre o endpoint e o lint de persona.
///
/// A primeira tentativa de ligar este lint recusou definição legítima com 422, porque foi aplicado
/// sobre campos que não eram escopo de caminho (`Stacks` é tecnologia, `Limitations` é texto livre).
/// Estes testes fixam as duas pontas da regra que a correção estabeleceu: persona que DECLARA
/// escopo é julgada; persona que não declara herda o escopo do papel e passa.
/// </summary>
public sealed class PersonaLintWiringTests
{
    [Fact]
    public void PersonaWithoutDeclaredScopeIsNotJudgedByPathRules()
    {
        // Espelha o que o endpoint faz quando `AllowedScopes` vem vazio: não chama o lint.
        // A definição canônica cai neste caso, e recusá-la seria barrar o legítimo.
        var definition = new PersonaDefinition("worker-frontend", "Frontend", ["read", "write"], [], []);

        // Quando o escopo É declarado, a ausência de denylist deixa de ser omissão tolerável.
        Assert.False(PersonaDefinitionLint.Inspect(definition with { AllowedScopes = ["src/**"] }).Accepted);
    }

    [Fact]
    public void ArbitraryExecutionPlusRepositoryWideWriteIsRefused()
    {
        var result = PersonaDefinitionLint.Inspect(new PersonaDefinition(
            "worker-livre", "Sem limite", ["bash", "write"], ["."], ["governance/**"]));

        Assert.False(result.Accepted);
        Assert.Contains(
            result.Findings,
            finding => finding.Code is PersonaDefinitionLint.DangerousToolCombination
                or PersonaDefinitionLint.ScopeTooBroad);
    }

    [Fact]
    public void ScopedPersonaWithDenylistIsAccepted()
    {
        var result = PersonaDefinitionLint.Inspect(new PersonaDefinition(
            "worker-delivery",
            "Entregas",
            ["read", "write"],
            ["src/Modules/Harness.Modules.Delivery/**"],
            ["governance/**", "coordination/**", ".github/**"]));

        Assert.True(result.Accepted, string.Join(" ", result.Findings.Select(f => f.Message)));
    }

    [Fact]
    public void ProtectedPathCannotBeGrantedAsAllowedScope()
    {
        var result = PersonaDefinitionLint.Inspect(new PersonaDefinition(
            "worker-governanca",
            "Governança",
            ["read"],
            ["governance/**"],
            ["src/**"]));

        Assert.False(result.Accepted);
    }
}
