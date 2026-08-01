using Harness.Modules.Governance.Context;
using Harness.Modules.Governance.Documentation;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Governance;
using Harness.SharedKernel.Security;
using Microsoft.Extensions.Logging;

namespace Harness.Host.Governance;

/// <summary>
/// Fase 1D — leva as SKILLS PROMOVIDAS até o bundle de contexto do agente.
///
/// O ciclo de aprendizado já ia inteiro até <c>Promoted</c>: proposta, avaliação, execução em
/// sombra, aprovação humana, promoção — tudo persistido e auditado. E parava ali. Nenhum código
/// lia uma skill promovida, então nada do que a fábrica aprendia voltava para quem executa: o
/// mesmo erro podia ser corrigido, virar skill aprovada e ser cometido de novo na tarefa seguinte.
///
/// O ESCOPO vem da PERSONA declarada no candidato (<c>AllowedScopes</c>, B8/F17): uma skill
/// aprendida por um especialista de frontend não vira instrução num card de banco. Skill sem
/// persona declarada vale para o projeto inteiro, que é o escopo em que ela foi promovida.
/// </summary>
public sealed partial class PromotedSkillProvider(
    ILearningCandidateStore candidates,
    IAgentCatalogStore agents,
    ILogger<PromotedSkillProvider> logger)
{
    /// <summary>
    /// Teto de skills por bundle. Não é orçamento — o orçamento de tokens do bundle já corta o que
    /// não couber; é proteção contra ler centenas de linhas do banco a cada tentativa.
    /// </summary>
    private const int MaxSkills = 20;

    private readonly ILearningCandidateStore _candidates =
        candidates ?? throw new ArgumentNullException(nameof(candidates));
    private readonly IAgentCatalogStore _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly ILogger<PromotedSkillProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Skills promovidas do projeto, prontas para o bundle. Uma falha de leitura NÃO derruba a
    /// tentativa: o agente executa sem as skills e o motivo fica no log — perder uma ajuda é
    /// muito menos grave do que perder a execução.
    /// </summary>
    public async Task<IReadOnlyList<ContextSkillSlice>> ListForProjectAsync(
        string tenantId,
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await _candidates.ListAsync(
                tenantId, null, projectId,
                LearningCandidateType.Skill, LearningCandidateState.Promoted,
                null, MaxSkills, cancellationToken);
            if (page.Items.Count == 0)
            {
                return [];
            }

            var slices = new List<ContextSkillSlice>(page.Items.Count);
            foreach (var candidate in page.Items)
            {
                var instructions = candidate.Payload.Instructions;
                if (string.IsNullOrWhiteSpace(instructions))
                {
                    continue;
                }

                // Uma skill promovida é texto que veio de execução real e vai DIRETO para o prompt
                // de outro agente. Se carregar um segredo colhido de um log, ele vaza para todas as
                // tentativas seguintes — a skill inteira é descartada, não redigida: instrução pela
                // metade é pior do que instrução nenhuma.
                if (SecretTextProtector.ContainsSecret(instructions) ||
                    SecretTextProtector.ContainsSecret(candidate.Payload.Title))
                {
                    LogSkillWithSecret(_logger, candidate.CandidateId);
                    continue;
                }

                var scopes = await ResolveScopesAsync(tenantId, candidate.Payload.PersonaId, cancellationToken);
                slices.Add(new ContextSkillSlice(
                    candidate.CandidateId,
                    candidate.Payload.Title,
                    instructions,
                    scopes,
                    $"learning-candidate:{candidate.CandidateId}@{candidate.ActiveVersion ?? candidate.ProposedVersion}",
                    GovernanceManifestSynchronizer.EstimateTokens(instructions)));
            }

            return slices;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSkillsUnavailable(_logger, projectId, exception);
            return [];
        }
    }

    /// <summary>
    /// Escopo da persona dona da skill. Persona ausente, apagada ou sem escopo declarado devolve
    /// lista vazia, que o builder lê como "vale para o projeto" — a skill foi promovida naquele
    /// projeto e não há caminho declarado que a restrinja mais do que isso.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveScopesAsync(
        string tenantId, string? personaId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(personaId))
        {
            return [];
        }

        var definition = await _agents.GetDefinitionForTenantAsync(tenantId, personaId, cancellationToken);

        // Sem escopo declarado o alcance é o PROJETO em que a skill foi promovida. O papel da
        // definição (`chief`/`specialist`) não serve de fallback: quem carrega escopo de caminho por
        // papel é a CONTA (`AgentRoles.PathScopesFor`), e passar o papel da persona por ali
        // devolveria lista vazia sempre — um filtro que parece existir e nunca filtra nada.
        return definition?.AllowedScopes ?? [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skills promovidas indisponíveis para o projeto {ProjectId} — a tentativa segue sem elas.")]
    private static partial void LogSkillsUnavailable(ILogger logger, string projectId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Skill promovida {CandidateId} DESCARTADA do bundle — o texto contém o que parece ser um segredo.")]
    private static partial void LogSkillWithSecret(ILogger logger, string candidateId);
}
