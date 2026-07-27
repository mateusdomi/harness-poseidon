namespace Harness.Persistence.Abstractions.Agents;

/// <summary>
/// Fila durável das solicitações estruturadas que os agentes fazem À CHEFE.
///
/// Antes disto, um executor que precisava de uma informação só tinha um caminho: falhar, ser
/// reprovado e escalar depois de N ciclos. A pergunta não é defeito do trabalho, e tratá-la como
/// tal custava uma tentativa inteira para descobrir uma frase. A prosa dele também não chegava a
/// lugar nenhum — a colheita lê o id da tentativa e a branch, nunca o texto.
/// </summary>
public interface IAgentRequestStore
{
    /// <summary>
    /// Registra a solicitação. IDEMPOTENTE por (tarefa, tentativa, tipo, pergunta) enquanto ela
    /// estiver aberta: o mesmo agente repetindo a mesma pergunta depois de um reinício não abre
    /// uma segunda entrada na fila da chefe.
    /// </summary>
    Task<(AgentRequestRecord Request, bool Created)> OpenAsync(
        AgentRequestOpenCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Responde. A resposta é recusada quando o fencing da tentativa não bate: uma tentativa
    /// substituída (por recuperação ou troca de conta) não pode consumir a resposta destinada à
    /// que perguntou, nem a antiga pode responder pela nova.
    /// </summary>
    Task<AgentRequestRecord?> AnswerAsync(
        AgentRequestAnswerCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentRequestRecord>> ListOpenAsync(
        string tenantId, string? projectId, int limit, CancellationToken cancellationToken = default);

    Task<AgentRequestRecord?> GetAsync(
        string tenantId, string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca como superada toda solicitação aberta de uma tentativa que deixou de existir — a
    /// tentativa morreu, foi cancelada ou trocou de conta. Sem isto a chefe responderia a uma
    /// pergunta cujo autor não pode mais ouvir, e a fila cresceria com trabalho fantasma.
    /// </summary>
    Task<int> SupersedeForAttemptAsync(
        string tenantId, string attemptId, string reasonCode, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}

public sealed record AgentRequestOpenCommand(
    string TenantId,
    string RequestId,
    string ProjectId,
    string TaskId,
    string? AttemptId,
    string Kind,
    string Question,
    string Reason,
    IReadOnlyList<string> Options,
    string? RecommendedOption,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RequestedPaths,
    bool Blocking,
    long FencingToken,
    DateTimeOffset OccurredAt);

public sealed record AgentRequestAnswerCommand(
    string TenantId,
    string RequestId,
    string State,
    string AnsweredBy,
    string Answer,
    string ReasonCode,
    long FencingToken,
    DateTimeOffset OccurredAt);

public sealed record AgentRequestRecord(
    string TenantId,
    string RequestId,
    string ProjectId,
    string TaskId,
    string? AttemptId,
    string Kind,
    string Question,
    string Reason,
    IReadOnlyList<string> Options,
    string? RecommendedOption,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RequestedPaths,
    bool Blocking,
    string State,
    string? AnsweredBy,
    string? Answer,
    string? AnswerReasonCode,
    long FencingToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AnsweredAt);
