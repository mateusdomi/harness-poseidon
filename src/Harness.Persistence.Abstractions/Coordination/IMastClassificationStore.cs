namespace Harness.Persistence.Abstractions.Coordination;

/// <summary>
/// O modo de falha atribuído a uma tentativa encerrada, com procedência.
/// <see cref="ClassifiedBy"/> e <see cref="Evidence"/> existem porque distribuição sem procedência
/// é número que ninguém pode contestar — e um número incontestável sobre falha é perigoso.
/// </summary>
public sealed record MastAttemptClassificationRecord(
    string TenantId,
    string AttemptId,
    string ProjectId,
    string TaskId,
    string FailureModeCode,
    string Category,
    string ClassifiedBy,
    string? Evidence,
    DateTimeOffset OccurredAt);

/// <summary>
/// Persistência da classificação MAST (migration 0105).
///
/// A tentativa tem UM modo: reclassificar substitui. Acumular contaria a mesma falha várias vezes
/// e o painel de distribuição — que existe para dizer ONDE o sistema erra mais — passaria a mentir
/// exatamente sobre a concentração que deveria revelar.
/// </summary>
public interface IMastClassificationStore
{
    /// <summary>Grava (ou substitui) a classificação da tentativa.</summary>
    Task ClassifyAsync(
        MastAttemptClassificationRecord record, CancellationToken cancellationToken = default);

    /// <summary>Classificações do projeto — insumo da distribuição exibida no modo Técnico.</summary>
    Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByProjectAsync(
        string tenantId, string projectId, CancellationToken cancellationToken = default);

    /// <summary>Classificações da tarefa — o que a Bruna lê antes de decidir como replanejar.</summary>
    Task<IReadOnlyList<MastAttemptClassificationRecord>> ListByTaskAsync(
        string tenantId, string taskId, CancellationToken cancellationToken = default);
}
