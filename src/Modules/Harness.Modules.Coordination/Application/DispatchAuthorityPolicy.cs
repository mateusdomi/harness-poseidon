namespace Harness.Modules.Coordination.Application;

/// <summary>Estado de conclusão de uma tentativa — o que torna o desfecho contável uma única vez.</summary>
public sealed record TurnCompletionState(
    string AttemptId,
    bool Completed = false,
    string? CompletionKind = null,
    string? IdempotencyKey = null);

public enum TurnCompletionOutcome
{
    /// <summary>Primeira conclusão: aplicada.</summary>
    Applied = 0,

    /// <summary>Mesma conclusão reenviada: aceita sem duplicar efeito.</summary>
    IdempotentReplay = 1,

    /// <summary>Já concluída por outro desfecho: recusada.</summary>
    AlreadyCompleted = 2
}

public sealed record TurnCompletionDecision(
    TurnCompletionOutcome Outcome,
    TurnCompletionState State,
    string ReasonCode);

/// <summary>
/// Conclusão EXATAMENTE UMA VEZ, inclusive em falha (B5).
///
/// O ponto sutil é a falha. Sucesso costuma ser reportado com cuidado; fracasso costuma terminar
/// em silêncio — o processo morre, o log some, e a espera fica aberta até alguma temporização
/// decidir por ela. Aqui a falha é um desfecho de primeira classe: ela também conclui, também
/// carrega carga estruturada e também é contada uma única vez.
///
/// Reenvio com a MESMA chave de idempotência é aceito como repetição (a rede é falível e o agente
/// deve poder repetir sem medo); desfecho diferente depois de concluído é recusado, porque a
/// segunda versão da história não pode reescrever a primeira.
/// </summary>
public static class TurnCompletionPolicy
{
    public const string ReasonApplied = "completion.applied";
    public const string ReasonReplay = "completion.idempotent_replay";
    public const string ReasonAlreadyCompleted = "completion.already_completed";

    public static TurnCompletionDecision Report(
        TurnCompletionState state,
        string completionKind,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(completionKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (!state.Completed)
        {
            return new TurnCompletionDecision(
                TurnCompletionOutcome.Applied,
                state with
                {
                    Completed = true,
                    CompletionKind = completionKind,
                    IdempotencyKey = idempotencyKey
                },
                ReasonApplied);
        }

        // Repetição fiel da mesma conclusão: o agente tem direito de reenviar sem medo.
        if (string.Equals(state.IdempotencyKey, idempotencyKey, StringComparison.Ordinal) &&
            string.Equals(state.CompletionKind, completionKind, StringComparison.Ordinal))
        {
            return new TurnCompletionDecision(TurnCompletionOutcome.IdempotentReplay, state, ReasonReplay);
        }

        return new TurnCompletionDecision(
            TurnCompletionOutcome.AlreadyCompleted, state, ReasonAlreadyCompleted);
    }

    /// <summary>Desfechos que contam como conclusão. Falha é um deles — silêncio não é.</summary>
    public static readonly IReadOnlySet<string> CompletionKinds =
        new HashSet<string>(["succeeded", "failed", "escalated"], StringComparer.Ordinal);

    public static bool IsCompletionKind(string kind) => CompletionKinds.Contains(kind);
}
