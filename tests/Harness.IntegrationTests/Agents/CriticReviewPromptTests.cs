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
}
