using Harness.Host.Governance;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Governance;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.Organizations;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Governance;

/// <summary>
/// Fase 1D — o consumo das SKILLS PROMOVIDAS, contra os stores reais.
///
/// Prova três coisas que só aparecem no caminho real:
///   * um candidato de skill que chegou a <c>Promoted</c> vira fatia de contexto — antes o ciclo
///     de aprendizado ia inteiro até a promoção e nada lia o resultado;
///   * a fatia carrega o ESCOPO da persona que a originou, para o builder poder filtrar;
///   * uma skill cujo texto contenha o que parece um segredo é DESCARTADA — ela iria direto para
///     o prompt de outro agente, e um segredo colhido de um log vazaria em toda tentativa
///     seguinte.
/// </summary>
public sealed class PromotedSkillProviderTests
{
    [Fact]
    public async Task PromotedSkillsBecomeScopedSlicesAndSecretsAreDiscarded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"promoted-skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(root, "skills.db"));
            await using (dispatcher)
            {
                await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
                var profiles = new SqliteLocalProfileStore(dispatcher);
                var now = DateTimeOffset.UtcNow;
                var tenantId = UlidValue.New(now).ToString();
                var profile = (await profiles.CreateAsync(
                    new LocalProfileCreateCommand(
                        tenantId, "Poseidon", UlidValue.New(now.AddTicks(1)).ToString(),
                        "Operador", null, null, "pt-BR", now),
                    timeout.Token)).Profile!;

                var organizationId = UlidValue.New(now).ToString();
                await new SqliteOrganizationStore(dispatcher).CreateAsync(
                    new OrganizationCreateCommand(
                        tenantId, organizationId, "Poseidon", "poseidon", "personal",
                        new OrganizationBrandRecord(null, null, null, null), now),
                    timeout.Token);

                var projectId = UlidValue.New(now.AddMilliseconds(1)).ToString();
                await new SqliteProjectStore(dispatcher).CreateAsync(
                    new ProjectCreateCommand(
                        tenantId,
                        new ProjectRecord(
                            tenantId, projectId, organizationId, "Poseidon", "PSD", "Control plane",
                            "active", "medium", null, "local", "main", [],
                            new ProjectBrandRecord(null, null, null, null),
                            [profile.Id], 1, UlidValue.New(now.AddMilliseconds(2)).ToString(),
                            "autonomous", now, now, 0),
                        now.AddMilliseconds(3)),
                    timeout.Token);

                // A persona dona da skill declara o escopo de caminho (B8/F17).
                var agents = new SqliteAgentCatalogStore(dispatcher);
                var personaId = UlidValue.New(now.AddMilliseconds(4)).ToString();
                await agents.CreateDefinitionAsync(
                    new AgentDefinitionCreateCommand(
                        tenantId, profile.Id, personaId,
                        new AgentDefinitionContent(
                            "backend-especialista", "Especialista backend", "specialist",
                            null, "Trabalha no núcleo .NET.", null, [], [], null, null, [], [], [],
                            null, [], AllowedScopes: ["src/Harness.Host"]),
                        now.AddMilliseconds(5)),
                    timeout.Token);

                var candidates = new SqliteLearningCandidateStore(dispatcher);
                var cleanId = await PromoteSkillAsync(
                    candidates, tenantId, organizationId, projectId, profile.Id,
                    "Medir o quadro antes de confiar no marcador",
                    "Antes de tratar o plano como materializado, contar os cards no quadro.",
                    personaId, now.AddMilliseconds(10), timeout.Token);

                // Segredo composto em tempo de execução: escrito inteiro no fonte, o próprio
                // scan-secrets do repositório reprovaria este arquivo — e um allowlist de scanner
                // é dívida que um dia esconde um segredo de verdade.
                var secret = "AKI" + "A" + new string('7', 16);
                var leakedId = await PromoteSkillAsync(
                    candidates, tenantId, organizationId, projectId, profile.Id,
                    "Skill com credencial vazada",
                    $"Use a chave {secret} para autenticar no serviço.",
                    personaId, now.AddMilliseconds(20), timeout.Token);

                var provider = new PromotedSkillProvider(
                    candidates, agents, NullLogger<PromotedSkillProvider>.Instance);
                var slices = await provider.ListForProjectAsync(tenantId, projectId, timeout.Token);

                var slice = Assert.Single(slices);
                Assert.Equal(cleanId, slice.SkillId);
                Assert.Equal(["src/Harness.Host"], slice.PathScopes);
                Assert.Contains(cleanId, slice.CitationReference, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, slice.Instructions, StringComparison.Ordinal);
                Assert.DoesNotContain(slices, item => item.SkillId == leakedId);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // limpeza best-effort
            }
        }
    }

    /// <summary>
    /// Leva um candidato de skill pelo ciclo REAL até <c>Promoted</c>: revisão, avaliação por
    /// agente independente, sombra, aprovação e promoção por administrador. Nenhum atalho — as
    /// mesmas guardas que o produto aplica.
    /// </summary>
    private static async Task<string> PromoteSkillAsync(
        SqliteLearningCandidateStore store,
        string tenantId,
        string organizationId,
        string projectId,
        string adminProfileId,
        string title,
        string instructions,
        string personaId,
        DateTimeOffset at,
        CancellationToken token)
    {
        var payload = new LearningCandidatePayload(
            title, null, instructions, personaId, null, null, null, null, null, null, null, null);
        LearningEvidenceRecord[] evidence =
            [new("test", $"test://{title}", "sha256:" + new string('a', 64), "Observado em execução real.")];
        var observation = $"Observação de {title}";
        var candidateId = UlidValue.New(at).ToString();
        var created = await store.CreateAsync(
            new LearningCandidateCreateCommand(
                tenantId, organizationId, projectId, candidateId, LearningCandidateType.Skill,
                LearningCandidatePolicy.ComputeFingerprint(
                    LearningCandidateType.Skill, observation, evidence, payload),
                observation, evidence, payload, "actor-agent", "fake", "actor-model",
                "skill/1", "skill/2", $"promote-{candidateId}", new string('b', 64), at),
            token);

        var current = created.Candidate;
        LearningCandidateTransitionCommand Command(
            LearningCandidateAction action,
            string note,
            string? evaluator = null,
            string? verdict = null,
            LearningShadowResult? shadow = null) =>
            new(tenantId, current.CandidateId, action, current.Version, adminProfileId, true, note,
                evaluator, evaluator is null ? null : "independent", evaluator is null ? null : "critic-model",
                verdict, shadow, $"{current.CandidateId}-{action}", new string('c', 64), at);

        current = await store.TransitionAsync(Command(LearningCandidateAction.RequestReview, "review"), token);
        current = await store.TransitionAsync(Command(LearningCandidateAction.RequestEvaluation, "evaluation-request"), token);
        current = await store.TransitionAsync(
            Command(LearningCandidateAction.CompleteEvaluation, "evaluation", "critic-agent", "pass"), token);
        current = await store.TransitionAsync(
            Command(LearningCandidateAction.StartShadow, "shadow",
                shadow: new LearningShadowResult(30, 0.12m, -0.08m, -120, -0.05m, 0, "evidence://shadow/1")),
            token);
        current = await store.TransitionAsync(Command(LearningCandidateAction.Approve, "approved"), token);
        current = await store.TransitionAsync(Command(LearningCandidateAction.Promote, "promoted"), token);
        Assert.Equal(LearningCandidateState.Promoted, current.State);
        return current.CandidateId;
    }
}
