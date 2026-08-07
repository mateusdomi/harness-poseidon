using Harness.Host.Agents;

namespace Harness.IntegrationTests.Agents;

public sealed class CriticReviewPromptTests
{
    [Fact]
    public void ReviewReceivesTheVersionedDelegationPackageAndProvenanceGate()
    {
        const string instruction =
            "FATO EXPLÍCITO DO USUÁRIO: avisar antes da renovação.\n" +
            "Definição de pronto: toda inferência deve estar rotulada.";
        var prompt = AgentRunOrchestrator.BuildCriticPrompt(
            new AgentCriticReviewCommand
            {
                AttemptId = "attempt",
                CriticAlias = "critic",
                ActorAlias = "actor",
                ReviewDirectory = "/repo",
                Diff = "+ gasto mensal é uma necessidade do usuário",
                DelegationInstruction = instruction,
            });

        Assert.Contains(instruction, prompt, StringComparison.Ordinal);
        Assert.Contains("TODO o pacote versionado", prompt, StringComparison.Ordinal);
        Assert.Contains("afirmação for apresentada como fato humano", prompt, StringComparison.Ordinal);
        Assert.Contains("fonte citada", prompt, StringComparison.Ordinal);
        Assert.Contains("rótulo explícito de inferência", prompt, StringComparison.Ordinal);
        Assert.Contains("auditoria afirmação por afirmação", prompt, StringComparison.Ordinal);
        Assert.Contains("checks.unsupportedClaims", prompt, StringComparison.Ordinal);
        Assert.Contains("checks.unlabeledInferences", prompt, StringComparison.Ordinal);
        Assert.Contains("governança interna não são declaração", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDelegationPackageIsExplicitlyInsufficientEvidence()
    {
        var prompt = AgentRunOrchestrator.BuildCriticPrompt(
            new AgentCriticReviewCommand
            {
                AttemptId = "attempt",
                CriticAlias = "critic",
                ActorAlias = "actor",
                ReviewDirectory = "/repo",
                Diff = "+ change",
            });

        Assert.Contains("evidência é insuficiente", prompt, StringComparison.Ordinal);
        Assert.Contains("não presuma o objetivo", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductValidationSwitchesToTreeLevelValidatorPrompt()
    {
        // Perfil v2: o validador de produto explora a ÁRVORE (não o diff) e recebe o perfil
        // efetivo como régua de stack — a lição do caso Indicadores da avaliação TrensRJ.
        var prompt = AgentRunOrchestrator.BuildCriticPrompt(
            new AgentCriticReviewCommand
            {
                AttemptId = "attempt",
                CriticAlias = "critic",
                ActorAlias = "actor",
                ReviewDirectory = "/worktree-validacao",
                Diff = "(diff irrelevante para validação de produto)",
                DelegationInstruction = "Objetivo 1: fatia vertical navegável.",
                ProductValidation = true,
                EffectiveProfileSummary = "Web · Backend .NET 8 · Frontend React · Oracle",
                AcceptanceCriteria = ["login funcional contra o banco real"],
            });

        Assert.Contains("VALIDADOR DE PRODUTO", prompt, StringComparison.Ordinal);
        Assert.Contains("EXPLORE-A", prompt, StringComparison.Ordinal);
        Assert.Contains("Web · Backend .NET 8 · Frontend React · Oracle", prompt, StringComparison.Ordinal);
        Assert.Contains("login funcional contra o banco real", prompt, StringComparison.Ordinal);
        Assert.Contains("desvio sem ADR aprovado", prompt, StringComparison.Ordinal);
        // O objeto da validação é a árvore: o diff NÃO é apresentado ao validador.
        Assert.DoesNotContain("## Diff sob revisão", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryReviewStillGetsTheDiffPrompt()
    {
        var prompt = AgentRunOrchestrator.BuildCriticPrompt(
            new AgentCriticReviewCommand
            {
                AttemptId = "attempt",
                CriticAlias = "critic",
                ActorAlias = "actor",
                ReviewDirectory = "/repo",
                Diff = "+ mudanca",
                DelegationInstruction = "Card comum.",
            });

        Assert.Contains("## Diff sob revisão", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("VALIDADOR DE PRODUTO", prompt, StringComparison.Ordinal);
    }
}
