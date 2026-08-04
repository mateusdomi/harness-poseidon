namespace Harness.Persistence.Abstractions.Governance;

public enum GovernanceReceiptState
{
    Selected,
    Delivered,
    Completed,
    Failed,
}

public static class GovernanceReceiptLifecycle
{
    public static bool CanTransition(GovernanceReceiptState current, GovernanceReceiptState next) =>
        (current, next) switch
        {
            (GovernanceReceiptState.Selected, GovernanceReceiptState.Delivered or GovernanceReceiptState.Completed or GovernanceReceiptState.Failed) => true,
            (GovernanceReceiptState.Delivered, GovernanceReceiptState.Completed or GovernanceReceiptState.Failed) => true,
            (GovernanceReceiptState.Failed, GovernanceReceiptState.Delivered) => true,

            // RETOMADA. Um turno interrompido no meio da invocação (queda do Host, lease vencida)
            // deixa o recibo em `delivered`; a nova tentativa refaz o caminho e entrega o MESMO
            // bundle outra vez. Sem esta repetição idempotente, a transição era recusada como
            // "stale", a tentativa morria em conflito e o turno esgotava as retentativas sem nunca
            // sair do lugar — a mensagem do usuário ficava sem resposta para sempre. A repetição é
            // registrada como uma nova versão do recibo: o ledger mostra as duas entregas, e o que
            // é idempotente é o direito de repetir, não o registro do fato.
            (GovernanceReceiptState.Delivered, GovernanceReceiptState.Delivered) => true,
            _ => false,
        };
}

public enum GovernanceMetricKind
{
    Selected,
    Delivered,
    OpenedByTool,
    RuleTriggered,
    ViolationDetected,
    ItemTruncated,
    GateResult,
    PatchApplied,
    PatchRejected,
    EvaluatorVerdict,
}

public sealed record GovernanceReceiptDocumentRecord(
    string DocumentId,
    string Checksum,
    string SelectionReason,
    string LoadPolicy,
    int EstimatedTokens);

/// <summary>
/// Um item que a montagem selecionou e o orçamento cortou, com o motivo. Só a lista de ids não
/// permitia distinguir "a regra não foi selecionada" de "foi selecionada e não coube".
/// </summary>
public sealed record GovernanceReceiptTruncationRecord(
    string SourceId,
    string Reason,
    string LoadPolicy,
    int EstimatedTokens);

/// <summary>
/// O contexto EFETIVO de uma execução, além dos documentos: quem executou, sob qual papel, em que
/// fase de qual workflow, com qual tipo de card, sob qual baseline e qual perfil efetivo, e o que
/// foi sobrescrito.
///
/// Existe para tornar respondível a pergunta que hoje termina em "o agente fez errado": a regra
/// existia? estava ativa? foi selecionada? foi carregada? foi truncada? qual versão? qual override
/// estava valendo? qual persona recebeu? — e só então, se todas derem sim, "mesmo assim violou".
///
/// Todos os campos são opcionais: recibos históricos não os têm, e inventar valor para satisfazer
/// o desserializador transformaria ausência de dado em dado errado.
/// </summary>
public sealed record GovernanceReceiptContextRecord(
    string? PersonaKey = null,
    string? AgentRole = null,
    string? Workflow = null,
    string? Phase = null,
    string? CardType = null,
    string? BaselineVersion = null,
    string? EffectiveProfileFingerprint = null,
    IReadOnlyList<string>? Overrides = null,
    IReadOnlyList<string>? ActiveAdrs = null,
    IReadOnlyList<GovernanceReceiptTruncationRecord>? Truncations = null,

    /// <summary>
    /// Ponte para a prova completa. O recibo NÃO duplica o conjunto de evidências — guarda a
    /// referência, e quem investiga navega dela até o ledger.
    /// </summary>
    string? EvidenceSetId = null,

    string? EvidenceCommitSha = null,

    string? GateDecision = null);

public sealed record GovernanceTurnReceiptRecord(
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string TurnId,
    string AgentId,
    string ManifestVersion,
    IReadOnlyList<GovernanceReceiptDocumentRecord> Documents,
    int EstimatedTokens,
    int? ActualPromptTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string Provider,
    string? Model,
    DateTimeOffset Timestamp,
    string BundleChecksum,
    GovernanceReceiptState State,
    string? GateResult,
    long Version,

    /// <summary>Contexto efetivo da execução. Nulo em recibos anteriores a este campo.</summary>
    GovernanceReceiptContextRecord? Context = null);

public sealed record GovernanceTurnReceiptCreateCommand(
    string TenantId,
    string ProjectId,
    string TaskId,
    string AttemptId,
    string TurnId,
    string AgentId,
    string ManifestVersion,
    IReadOnlyList<GovernanceReceiptDocumentRecord> Documents,
    int EstimatedTokens,
    IReadOnlyList<string> Truncated,
    IReadOnlyList<string> Conflicts,
    int CacheHits,
    string Provider,
    string? Model,
    DateTimeOffset Timestamp,
    string BundleChecksum,

    /// <summary>Contexto efetivo da execução; opcional para não quebrar produtores existentes.</summary>
    GovernanceReceiptContextRecord? Context = null);

public sealed record GovernanceTurnReceiptCompleteCommand(
    string TenantId,
    string TurnId,
    long ExpectedVersion,
    int? ActualPromptTokens,
    GovernanceReceiptState State,
    string? GateResult,
    DateTimeOffset OccurredAt);

public sealed record GovernanceMetricAppendCommand(
    string TenantId,
    string ProjectId,
    string TurnId,
    string EventId,
    GovernanceMetricKind Kind,
    string? DocumentId,
    string? RuleId,
    string? DetailCode,
    int? TokenCount,
    DateTimeOffset OccurredAt);

public sealed record GovernanceMetricRecord(
    string TenantId,
    string ProjectId,
    string TurnId,
    string EventId,
    GovernanceMetricKind Kind,
    string? DocumentId,
    string? RuleId,
    string? DetailCode,
    int? TokenCount,
    DateTimeOffset OccurredAt);

public sealed record ContextSnapshotSourceRecord(
    string SourceId,
    string Kind,
    string CitationReference);

public sealed record ContextSnapshotCreateCommand(
    string TenantId,
    string SnapshotId,
    string ProjectId,
    string WorkTaskId,
    string ExecutionId,
    string ManifestVersion,
    IReadOnlyList<string> BundleManifestIds,
    IReadOnlyList<ContextSnapshotSourceRecord> Sources,
    string AssembledContextHash,
    int TokenCount,
    DateTimeOffset CreatedAt);

public sealed record ContextSnapshotRecord(
    string TenantId,
    string SnapshotId,
    string ProjectId,
    string WorkTaskId,
    string ExecutionId,
    string ManifestVersion,
    IReadOnlyList<string> BundleManifestIds,
    IReadOnlyList<ContextSnapshotSourceRecord> Sources,
    string AssembledContextHash,
    int TokenCount,
    DateTimeOffset CreatedAt);

/// <summary>
/// Liga um recibo já gravado ao conjunto de evidências que decidiu o portão daquela tentativa.
///
/// Existe como operação SEPARADA por causa do ciclo de vida real: o recibo nasce quando o turno
/// começa, e o portão do produto só decide depois de o trabalho ser entregue e verificado. Inventar
/// um id de evidência na criação transformaria ausência de dado em dado errado; por isso o campo
/// nasce nulo e é preenchido quando existe o que preencher.
/// </summary>
public sealed record GovernanceReceiptEvidenceLinkCommand(
    string TenantId,
    string AttemptId,
    string EvidenceSetId,
    string EvidenceCommitSha,
    string GateDecision,
    DateTimeOffset OccurredAt);

public interface IGovernanceRuntimeStore
{
    Task<GovernanceTurnReceiptRecord> CreateReceiptAsync(
        GovernanceTurnReceiptCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<GovernanceTurnReceiptRecord> CompleteReceiptAsync(
        GovernanceTurnReceiptCompleteCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Preenche a ponte recibo → evidência dos recibos da tentativa. Devolve quantos foram ligados;
    /// zero significa que a tentativa não produziu recibo, o que é fato a registrar, não erro.
    /// </summary>
    Task<int> LinkEvidenceAsync(
        GovernanceReceiptEvidenceLinkCommand command,
        CancellationToken cancellationToken = default);

    Task<GovernanceTurnReceiptRecord?> GetReceiptAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernanceTurnReceiptRecord>> ListReceiptsAsync(
        string tenantId,
        string? projectId,
        string? afterTurnId,
        int limit,
        CancellationToken cancellationToken = default);

    Task AppendMetricAsync(
        GovernanceMetricAppendCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernanceMetricRecord>> ListMetricsAsync(
        string tenantId,
        string turnId,
        CancellationToken cancellationToken = default);

    Task<ContextSnapshotRecord> CreateContextSnapshotAsync(
        ContextSnapshotCreateCommand command,
        CancellationToken cancellationToken = default);

    Task<ContextSnapshotRecord?> GetContextSnapshotAsync(
        string tenantId,
        string snapshotId,
        CancellationToken cancellationToken = default);
}

public sealed class GovernanceRuntimeConflictException(string message) : Exception(message);
