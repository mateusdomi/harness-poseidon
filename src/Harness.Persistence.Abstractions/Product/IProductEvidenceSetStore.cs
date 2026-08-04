namespace Harness.Persistence.Abstractions.Product;

/// <summary>
/// O conjunto de evidências que decidiu um portão, como persistido. É o que permite responder,
/// meses depois, "exatamente quais evidências fizeram este projeto passar?".
///
/// Os itens, o plano e os achados vão em JSON versionado: eles evoluem por acréscimo (tipos novos
/// de evidência, campos novos de proveniência) e uma coluna por campo tornaria cada evolução uma
/// migração de schema. Sobem a coluna apenas os campos pelos quais se CONSULTA.
/// </summary>
public sealed record ProductEvidenceSetRecord(
    string TenantId,
    string EvidenceSetId,
    string ProjectId,
    string? WorkflowRunId,
    string? TaskId,
    string? AttemptId,
    string CommitSha,
    int ProfileVersion,
    string ProfileFingerprint,
    string Modality,
    string GateDecision,
    string PlanJson,
    string ItemsJson,
    string FindingsJson,
    string Collectors,
    DateTimeOffset CreatedAt);

/// <summary>
/// Ledger APPEND-ONLY dos conjuntos de evidência.
///
/// Não existe atualização de propósito: a tentativa 17 que reprovou e a 18 que passou são dois
/// fatos, e sobrescrever o primeiro apagaria justamente o que o Poseidon precisa para aprender por
/// que a primeira falhou.
/// </summary>
public interface IProductEvidenceSetStore
{
    /// <summary>Grava um conjunto. Idempotente pelo id; nunca sobrescreve conteúdo.</summary>
    Task<ProductEvidenceSetRecord> AppendAsync(
        ProductEvidenceSetRecord record,
        CancellationToken cancellationToken = default);

    Task<ProductEvidenceSetRecord?> GetAsync(
        string tenantId,
        string evidenceSetId,
        CancellationToken cancellationToken = default);

    /// <summary>Histórico do projeto, do mais recente para o mais antigo.</summary>
    Task<IReadOnlyList<ProductEvidenceSetRecord>> ListAsync(
        string tenantId,
        string projectId,
        int limit,
        CancellationToken cancellationToken = default);
}
