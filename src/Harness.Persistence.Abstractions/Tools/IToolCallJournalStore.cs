namespace Harness.Persistence.Abstractions.Tools;

/// <summary>
/// Fase 0B2: registro durável de uma chamada de ferramenta. Vive em Abstractions porque a
/// persistência não conhece o domínio de ferramentas — o broker traduz para cá através de um
/// adaptador no Host, e as camadas seguem separadas.
/// </summary>
public interface IToolCallJournalStore
{
    /// <summary>
    /// Chamada PERMITIDA já registrada para a chave, ou nulo. Uma negativa não é replayable: a
    /// política pode ter mudado, e negar de novo é barato.
    /// </summary>
    Task<ToolCallJournalEntry?> FindAllowedAsync(
        string tenantId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task RecordAsync(ToolCallJournalEntry entry, CancellationToken cancellationToken = default);
}

public sealed record ToolCallJournalEntry(
    string TenantId,
    string IdempotencyKey,
    string ProjectId,
    string CardId,
    string AttemptId,
    string AgentId,
    string Profile,
    string ToolId,
    long FencingToken,
    IReadOnlyList<string> Paths,
    bool NetworkEnabled,
    bool Mutating,
    bool Allowed,
    string Code,
    string Detail,
    string Output,
    bool OutputTruncated,
    int ExitCode,
    DateTimeOffset OccurredAt);
