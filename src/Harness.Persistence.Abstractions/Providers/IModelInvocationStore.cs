using Harness.SharedKernel.Providers;

namespace Harness.Persistence.Abstractions.Providers;

/// <summary>
/// Store de invocações e uso de tokens/custo de modelos LLM (Fase 3).
/// </summary>
public interface IModelInvocationStore
{
    Task RecordInvocationAsync(ModelInvocationRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelInvocationRecord>> GetTaskInvocationsAsync(string tenantId, string workTaskId, CancellationToken cancellationToken = default);
    Task<decimal> GetTotalCostAsync(string tenantId, string? projectId = null, CancellationToken cancellationToken = default);
}
