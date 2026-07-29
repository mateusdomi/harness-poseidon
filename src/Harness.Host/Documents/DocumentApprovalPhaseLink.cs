using Harness.Persistence.Abstractions.Workflows;
using Harness.SharedKernel.Time;

namespace Harness.Host.Documents;

/// <summary>O que aconteceu com a esteira quando o dono aprovou o documento.</summary>
public enum DocumentApprovalPhaseLinkOutcome
{
    /// <summary>O projeto não tem esteira em execução — a aprovação vale só para o documento.</summary>
    NoWorkflow,

    /// <summary>Nenhum objetivo-documento da fase corresponde a este documento.</summary>
    NoMatchingObjective,

    /// <summary>
    /// Mais de um objetivo-documento com o mesmo nome: ambiguidade não se resolve por chute.
    /// </summary>
    AmbiguousObjective,

    /// <summary>
    /// O objetivo ainda não chegou a <c>validated</c>: falta a prova de trabalho e de conferência
    /// de um colega. A aprovação do dono NÃO fabrica esses degraus.
    /// </summary>
    NotReadyForOwner,

    /// <summary>O objetivo já estava aprovado (reprocessamento).</summary>
    AlreadyApproved,

    /// <summary>O objetivo subiu ao último degrau por decisão do dono.</summary>
    Advanced,

    /// <summary>O motor recusou o avanço; o motivo fica em <c>Detail</c>.</summary>
    Rejected,
}

public sealed record DocumentApprovalPhaseLinkResult(
    DocumentApprovalPhaseLinkOutcome Outcome,
    string? RunId = null,
    string? PhaseKey = null,
    string? ObjectiveKey = null,
    string? Detail = null);

/// <summary>Objetivo-documento que corresponde a um documento, dentro de um run.</summary>
public sealed record DocumentApprovalPhaseMatch(
    string RunId,
    long RunVersion,
    string PhaseKey,
    string ObjectiveKey,
    string ObjectiveState);

/// <summary>
/// O DEGRAU HUMANO QUE NINGUÉM DAVA.
///
/// O objetivo de uma etapa sobe por degraus — <c>pending → executed → validated → approved</c> — e
/// o <see cref="Harness.Host.Workflows.WorkflowPhaseDriver"/> para de propósito em
/// <c>validated</c>: o último degrau é decisão de pessoa, não de máquina. Só que nenhuma superfície
/// do produto dava esse degrau. O dono aprovava o documento na tela, o documento ia para
/// <c>approved</c> — e a etapa continuava parada esperando um clique que não existia em lugar
/// nenhum. Era o defeito mais silencioso do fluxo: tudo indicava trabalho pronto, e a esteira não
/// andava.
///
/// Aqui a aprovação do dono passa a valer na esteira. Duas honestidades importam mais que a
/// conveniência de fazer a etapa andar:
///
/// 1. <b>O dono aprova; não executa nem confere.</b> O avanço parte apenas de <c>validated</c>. Se
///    o objetivo está abaixo disso, aprovar o documento NÃO inventa "trabalho feito" nem
///    "conferido por um colega" — o resultado é <see cref="DocumentApprovalPhaseLinkOutcome.NotReadyForOwner"/>
///    e a etapa continua mostrando o que de fato falta.
/// 2. <b>Sem correspondência, nada acontece.</b> O casamento é por nome exato do artefato dentro da
///    etapa do documento. Nome repetido é ambiguidade declarada, não escolha do código.
///
/// A aprovação do documento é durável ANTES desta ligação: se aqui nada casar, a decisão do dono
/// não se perde.
/// </summary>
public static class DocumentApprovalPhaseLink
{
    /// <summary>Tipo de objetivo cujo entregável é um documento.</summary>
    private const string DocumentKind = "document";

    /// <summary>Degrau que só uma pessoa pode dar.</summary>
    public const string OwnerStep = "approved";

    /// <summary>Degrau mínimo para o dono poder decidir: trabalho feito e conferido.</summary>
    public const string ReadyForOwnerState = "validated";

    /// <summary>
    /// Acha o objetivo-documento correspondente. Preferência absoluta pela etapa registrada no
    /// documento; sem etapa registrada, a etapa ativa do run. Nunca resolve empate por chute.
    /// </summary>
    public static DocumentApprovalPhaseMatch? Match(
        WorkflowRunAggregateSnapshot aggregate,
        string? documentPhaseName,
        string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            return null;
        }

        var title = documentTitle.Trim();
        var phases = string.IsNullOrWhiteSpace(documentPhaseName)
            ? aggregate.Phases
                .Where(phase => string.Equals(phase.State, "active", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : aggregate.Phases
                .Where(phase => string.Equals(
                    phase.Name.Trim(), documentPhaseName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var matches = phases
            .SelectMany(phase => phase.Objectives
                .Where(objective =>
                    string.Equals(objective.Kind, DocumentKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(objective.Name.Trim(), title, StringComparison.OrdinalIgnoreCase))
                .Select(objective => new DocumentApprovalPhaseMatch(
                    aggregate.RunId, aggregate.Version, phase.Key, objective.Key, objective.State)))
            .Take(2)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Verdadeiro quando há mais de um objetivo-documento com o mesmo nome na etapa — a única razão
    /// legítima para <see cref="Match"/> devolver nulo tendo candidatos.
    /// </summary>
    public static bool IsAmbiguous(
        WorkflowRunAggregateSnapshot aggregate,
        string? documentPhaseName,
        string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        if (string.IsNullOrWhiteSpace(documentTitle))
        {
            return false;
        }

        var title = documentTitle.Trim();
        var phases = string.IsNullOrWhiteSpace(documentPhaseName)
            ? aggregate.Phases
                .Where(phase => string.Equals(phase.State, "active", StringComparison.OrdinalIgnoreCase))
            : aggregate.Phases
                .Where(phase => string.Equals(
                    phase.Name.Trim(), documentPhaseName.Trim(), StringComparison.OrdinalIgnoreCase));

        return phases
            .SelectMany(phase => phase.Objectives)
            .Count(objective =>
                string.Equals(objective.Kind, DocumentKind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(objective.Name.Trim(), title, StringComparison.OrdinalIgnoreCase)) > 1;
    }

    /// <summary>
    /// Reflete na esteira a aprovação que o dono acabou de dar no documento. Idempotente: a chave
    /// carrega documento + objetivo + degrau, então reprocessar a mesma aprovação não gera segundo
    /// avanço.
    /// </summary>
    public static async Task<DocumentApprovalPhaseLinkResult> ApproveAsync(
        IWorkflowCatalogStore catalog,
        IWorkflowStore authority,
        IClock clock,
        string tenantId,
        string projectId,
        string documentId,
        string documentTitle,
        string? documentPhaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(clock);

        var bindings = await catalog.ListBindingsAsync(tenantId, projectId, null, 1, cancellationToken);
        if (bindings.Count == 0)
        {
            return new DocumentApprovalPhaseLinkResult(DocumentApprovalPhaseLinkOutcome.NoWorkflow);
        }

        var runs = await catalog.ListRunsAsync(tenantId, bindings[0].Id, null, 20, cancellationToken);
        var running = runs.FirstOrDefault(run =>
            string.Equals(run.State, "running", StringComparison.Ordinal));
        if (running is null)
        {
            return new DocumentApprovalPhaseLinkResult(DocumentApprovalPhaseLinkOutcome.NoWorkflow);
        }

        var aggregate = await authority.ReadRunAggregateAsync(tenantId, running.Id, cancellationToken);
        if (aggregate is null)
        {
            return new DocumentApprovalPhaseLinkResult(DocumentApprovalPhaseLinkOutcome.NoWorkflow);
        }

        var match = Match(aggregate, documentPhaseName, documentTitle);
        if (match is null)
        {
            return new DocumentApprovalPhaseLinkResult(
                IsAmbiguous(aggregate, documentPhaseName, documentTitle)
                    ? DocumentApprovalPhaseLinkOutcome.AmbiguousObjective
                    : DocumentApprovalPhaseLinkOutcome.NoMatchingObjective,
                running.Id);
        }

        if (string.Equals(match.ObjectiveState, OwnerStep, StringComparison.OrdinalIgnoreCase))
        {
            return new DocumentApprovalPhaseLinkResult(
                DocumentApprovalPhaseLinkOutcome.AlreadyApproved,
                match.RunId, match.PhaseKey, match.ObjectiveKey);
        }

        if (!string.Equals(match.ObjectiveState, ReadyForOwnerState, StringComparison.OrdinalIgnoreCase))
        {
            return new DocumentApprovalPhaseLinkResult(
                DocumentApprovalPhaseLinkOutcome.NotReadyForOwner,
                match.RunId, match.PhaseKey, match.ObjectiveKey, match.ObjectiveState);
        }

        var receipt = await authority.AdvanceObjectiveAsync(
            new WorkflowObjectiveAdvanceCommand(
                tenantId,
                match.RunId,
                match.PhaseKey,
                match.ObjectiveKey,
                OwnerStep,
                match.RunVersion,
                $"document-approval:{documentId}:{match.ObjectiveKey}:{OwnerStep}",
                clock.UtcNow),
            cancellationToken);

        return receipt.Status is WorkflowRunMutationStatus.Applied
            or WorkflowRunMutationStatus.IdempotentReplay
            ? new DocumentApprovalPhaseLinkResult(
                DocumentApprovalPhaseLinkOutcome.Advanced,
                match.RunId, match.PhaseKey, match.ObjectiveKey)
            : new DocumentApprovalPhaseLinkResult(
                DocumentApprovalPhaseLinkOutcome.Rejected,
                match.RunId, match.PhaseKey, match.ObjectiveKey, receipt.Status.ToString());
    }
}
