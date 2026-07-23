namespace Harness.Persistence.Abstractions.Governance;

/// <summary>
/// Store durável das notas de contexto do Chief externalizadas pela <c>IContextStrategy</c>. A
/// memória vive aqui — no store, keyed por (tenant, project, [conversation]) — e não na janela
/// compactada, de modo que fatos/decisões críticos sobrevivem à compactação e ao reinício.
/// </summary>
public interface IChiefContextNoteStore
{
    /// <summary>
    /// Persiste as notas emitidas. Idempotente por (tenant, project, conversation, source_item_id):
    /// reprocessar o mesmo turno não duplica notas. Devolve quantas linhas NOVAS foram inseridas.
    /// </summary>
    Task<int> AppendAsync(ChiefContextNoteAppendCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lê as notas persistidas do escopo, das mais antigas para as mais recentes.</summary>
    Task<IReadOnlyList<ChiefContextNoteRecord>> ListAsync(
        string tenantId,
        string projectId,
        string? conversationId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Uma nota a persistir: id durável, item de origem, papel, conteúdo e ordenação.</summary>
public sealed record ChiefContextNoteEntry(
    string NoteId,
    string SourceItemId,
    string Role,
    string Content,
    int TokenEstimate,
    long Sequence);

public sealed record ChiefContextNoteAppendCommand(
    string TenantId,
    string ProjectId,
    string? ConversationId,
    string? TurnId,
    IReadOnlyList<ChiefContextNoteEntry> Notes,
    DateTimeOffset OccurredAt);

public sealed record ChiefContextNoteRecord(
    string TenantId,
    string ProjectId,
    string? ConversationId,
    string? TurnId,
    string NoteId,
    string SourceItemId,
    string Role,
    string Content,
    int TokenEstimate,
    long Sequence,
    DateTimeOffset CreatedAt);
