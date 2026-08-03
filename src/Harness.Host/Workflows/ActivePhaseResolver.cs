using Harness.Persistence.Abstractions.Workflows;

namespace Harness.Host.Workflows;

public sealed record ActiveWorkflowPhase(string Key, string Name, int Order);

/// <summary>
/// A FASE ATIVA da esteira de um projeto.
///
/// Existe porque os cards de trabalho nasciam sem fase nenhuma. Havia dois fluxos paralelos que
/// não se falavam: os cards de ARTEFATO, criados pela esteira e carimbados com a fase, e os cards
/// de TRABALHO, criados pela decomposição da demanda e sem fase alguma.
///
/// A consequência é exatamente a que se teme num sistema assim: o progresso da fase media apenas
/// os documentos. Uma fase de Desenvolvimento podia exibir "100%" com o briefing técnico, o code
/// review e as métricas DORA escritos — e nenhuma linha de código implementada. Documento
/// preenchido não é produto entregue.
///
/// Com o carimbo, o trabalho real entra na conta da fase, e o `PhaseObligationPlanner` passa a
/// pesar card de implementação junto com documento.
/// </summary>
public sealed class ActivePhaseResolver(
    IWorkflowCatalogStore catalog,
    IWorkflowStore authority)
{
    /// <summary>
    /// Ordem da fase a partir da qual o Playbook LIBERA construção.
    ///
    /// Existe como constante única porque dois lugares decidem sobre a mesma fronteira: quem
    /// ADIA os cards de implementação até ela chegar e quem IMPEDE a fase executiva de fechar
    /// sem eles. Enquanto o número estava escrito literalmente nos dois, mover a fronteira num
    /// deles abriria exatamente o buraco que o outro existe para tapar.
    /// </summary>
    public const int DevelopmentPhaseOrder = 5;

    private readonly IWorkflowCatalogStore _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly IWorkflowStore _authority =
        authority ?? throw new ArgumentNullException(nameof(authority));

    /// <summary>
    /// Nome da fase ativa, ou <see langword="null"/> quando o projeto ainda não tem esteira em
    /// execução. Nulo NÃO bloqueia a criação do card: um projeto sem run é um projeto que ainda
    /// vai ganhar um, e recusar trabalho por causa disso pararia a fábrica por uma formalidade.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string tenantId, string projectId, CancellationToken cancellationToken)
        => (await ResolveSnapshotAsync(tenantId, projectId, cancellationToken))?.Name;

    /// <summary>
    /// Resolve a identidade completa da fase ativa. A ordem é necessária para os gates de
    /// efeito: uma demanda capturada no chat durante Triagem deve continuar durável, mas seus
    /// cards de implementação não podem nascer antes da liberação da Fase 5.
    /// </summary>
    public async Task<ActiveWorkflowPhase?> ResolveSnapshotAsync(
        string tenantId, string projectId, CancellationToken cancellationToken)
    {
        try
        {
            var bindings = await _catalog.ListBindingsAsync(tenantId, projectId, null, 1, cancellationToken);
            if (bindings.Count == 0)
            {
                return null;
            }

            var runs = await _catalog.ListRunsAsync(tenantId, bindings[0].Id, null, 20, cancellationToken);
            var running = runs.FirstOrDefault(run =>
                string.Equals(run.State, "running", StringComparison.Ordinal));
            if (running is null)
            {
                return null;
            }

            var aggregate = await _authority.ReadRunAggregateAsync(tenantId, running.Id, cancellationToken);
            var phase = aggregate?.Phases
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.State, "active", StringComparison.Ordinal));
            return phase is null ? null : new ActiveWorkflowPhase(phase.Key, phase.Name, phase.Order);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Esteira indisponível não pode impedir o trabalho de existir. O card nasce sem fase e
            // a próxima passagem do condutor o reconcilia.
            return null;
        }
    }
}
