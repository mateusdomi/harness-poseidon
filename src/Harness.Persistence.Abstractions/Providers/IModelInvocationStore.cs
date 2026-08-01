using Harness.SharedKernel.Providers;

namespace Harness.Persistence.Abstractions.Providers;

/// <summary>
/// Store de invocações e uso de tokens/custo de modelos LLM (Fase 3).
/// </summary>
public interface IModelInvocationStore
{
    Task RecordInvocationAsync(ModelInvocationRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelInvocationRecord>> GetTaskInvocationsAsync(string tenantId, string workTaskId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Invocações do PROJETO, da mais recente para a mais antiga, limitadas a
    /// <paramref name="limit"/>.
    ///
    /// Existe porque medir produtividade card a card enviesa a medida: percorrer só os cards que
    /// já produziram uma classificação de falha mede o desempenho sobre o subconjunto em que ele
    /// foi pior, e chamar isso de "produtividade" mente para quem lê.
    /// </summary>
    Task<IReadOnlyList<ModelInvocationRecord>> GetProjectInvocationsAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken = default);

    Task<decimal> GetTotalCostAsync(string tenantId, string? projectId = null, CancellationToken cancellationToken = default);
}
