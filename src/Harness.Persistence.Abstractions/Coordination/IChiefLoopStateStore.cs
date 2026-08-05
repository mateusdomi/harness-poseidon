namespace Harness.Persistence.Abstractions.Coordination;

/// <summary>Uma entrada do estado durável do laço: backoff (instante) ou contador.</summary>
public sealed record ChiefLoopStateEntry(
    string Kind, string EntryId, DateTimeOffset? NotBefore, int Counter);

/// <summary>
/// O estado que explica as decisões do laço da chefe, persistido — porque um freio que zera no
/// reinício não é freio: é uma sugestão que a primeira queda apaga.
/// </summary>
public interface IChiefLoopStateStore
{
    /// <summary>Todo o estado do tenant, para hidratar o cache do laço no arranque.</summary>
    Task<IReadOnlyList<ChiefLoopStateEntry>> LoadAsync(
        string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Upsert idempotente de uma entrada.</summary>
    Task UpsertAsync(
        string tenantId, ChiefLoopStateEntry entry, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string tenantId, string kind, string entryId,
        CancellationToken cancellationToken = default);
}
