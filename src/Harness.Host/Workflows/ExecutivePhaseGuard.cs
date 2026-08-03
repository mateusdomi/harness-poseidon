using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.Workflows;

/// <summary>
/// Um compromisso de materialização visto pela regra: só o que ela precisa saber para decidir.
/// </summary>
/// <param name="DemandId">A demanda cujo plano vira card de implementação.</param>
/// <param name="Internal">
/// Demanda criada pela própria esteira para produzir um documento. Ela não representa construção
/// pedida pelo usuário e por isso não segura fase executiva.
/// </param>
/// <param name="CommitmentStatus">
/// Estado do compromisso durável, ou <see langword="null"/> quando a demanda nunca pediu
/// materialização — caso em que não há promessa pendente a cobrar.
/// </param>
public sealed record DemandMaterializationView(
    string DemandId,
    bool Internal,
    string? CommitmentStatus);

/// <summary>
/// DOCUMENTO NÃO CONCLUI FASE EXECUTIVA.
///
/// Os cards de implementação nascem de um compromisso DURÁVEL por demanda, e esse compromisso fica
/// deliberadamente adiado enquanto o Playbook não libera construção. Quem o cumpre é um
/// reconciliador com ritmo próprio, independente do ciclo do condutor de fase — então existe uma
/// janela real em que a fase já é executiva, os documentos dela estão todos aceitos e NENHUM card
/// de implementação nasceu ainda.
///
/// Sem esta guarda o portão aprovaria nessa janela, e a fase de Desenvolvimento fecharia com
/// briefing técnico, code review estruturado e métricas DORA escritos e nenhuma linha implementada
/// — o desfecho exato que a esteira existe para impedir.
///
/// O sinal é o COMPROMISSO, não a contagem de cards: contar cards confundiria "ainda não
/// materializou" com "materializou e o trabalho previsto era pequeno".
/// </summary>
public static class ExecutivePhaseGuard
{
    /// <summary>Prefixo estável do achado, para o motivo aparecer legível no ciclo.</summary>
    public const string FailurePrefix = "implementation_not_materialized";

    /// <summary>
    /// As demandas que prometeram implementação e ainda não a materializaram, em ordem estável.
    /// Vazio significa que a fase pode ser avaliada normalmente — nunca significa "não verifiquei".
    /// </summary>
    public static IReadOnlyList<string> PendingImplementation(
        int phaseOrder,
        IReadOnlyList<DemandMaterializationView> demands)
    {
        ArgumentNullException.ThrowIfNull(demands);

        // Antes da liberação de construção o adiamento é o comportamento CORRETO, não uma pendência:
        // cobrar card de implementação em Triagem pararia a esteira no lugar em que ela deve andar.
        if (phaseOrder < ActivePhaseResolver.DevelopmentPhaseOrder)
        {
            return [];
        }

        return
        [
            .. demands
                .Where(demand => !demand.Internal)
                .Where(demand => demand.CommitmentStatus is not null && !string.Equals(
                    demand.CommitmentStatus,
                    PlanMaterializationStatus.Completed,
                    StringComparison.Ordinal))
                .Select(demand => demand.DemandId)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];
    }
}
