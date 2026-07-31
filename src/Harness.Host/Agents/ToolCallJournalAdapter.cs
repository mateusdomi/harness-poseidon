using Harness.Modules.Tools.Application;
using Harness.Persistence.Abstractions.Tools;

namespace Harness.Host.Agents;

/// <summary>
/// Traduz o diário do broker (domínio de ferramentas) para o store durável (persistência). O
/// adaptador existe para que a persistência não precise conhecer o domínio — e para que o broker
/// não precise conhecer SQL.
/// </summary>
public sealed class ToolCallJournalAdapter(IToolCallJournalStore store) : IToolCallJournal
{
    private readonly IToolCallJournalStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<ToolCallResult?> FindAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var entry = await _store.FindAllowedAsync(tenantId, idempotencyKey, cancellationToken);
        return entry is null
            ? null
            : new ToolCallResult(
                entry.Allowed, entry.Code, entry.Detail, entry.Output, entry.OutputTruncated,
                false, entry.ExitCode);
    }

    public Task RecordAsync(
        ToolCallRequest request, ToolCallResult result, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        return _store.RecordAsync(
            new ToolCallJournalEntry(
                request.TenantId, request.IdempotencyKey, request.ProjectId, request.CardId,
                request.AttemptId, request.AgentId, request.Profile.ToString(), request.ToolId,
                request.FencingToken, request.Paths, request.Network.Enabled, request.Mutating,
                result.Allowed, result.Code, result.Detail, result.Output, result.OutputTruncated,
                result.ExitCode, occurredAt),
            cancellationToken);
    }
}
