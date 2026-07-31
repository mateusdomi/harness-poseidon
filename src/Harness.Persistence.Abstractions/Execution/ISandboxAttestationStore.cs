namespace Harness.Persistence.Abstractions.Execution;

/// <summary>
/// Fase 0B1 (BR-002/BR-013): guarda a prova de contenção da tentativa e o aceite explícito de modo
/// inseguro. Os dois são fatos auditáveis; nenhum deles é inferido em tempo de leitura.
/// </summary>
public interface ISandboxAttestationStore
{
    /// <summary>
    /// Grava a attestation da tentativa. Idempotente por (tenant, attempt): a primeira emissão é a
    /// que vale, e uma segunda não pode "melhorar" a avaliação de uma execução em curso.
    /// </summary>
    Task<SandboxAttestationRecord> SaveAsync(
        SandboxAttestationRecord record, CancellationToken cancellationToken = default);

    /// <summary>Attestation da tentativa, ou nulo quando ela nunca foi emitida.</summary>
    Task<SandboxAttestationRecord?> GetAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aceite VIGENTE do modo inseguro para o projeto, ou nulo. Vencido ou revogado conta como
    /// ausente — um aceite de ontem não autoriza a execução de hoje.
    /// </summary>
    Task<UnsafeExecutionAcceptanceRecord?> GetUnsafeAcceptanceAsync(
        string tenantId, string projectId, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Registra o aceite do proprietário. Substitui o anterior do mesmo projeto.</summary>
    Task<UnsafeExecutionAcceptanceRecord> AcceptUnsafeAsync(
        UnsafeExecutionAcceptanceRecord record, CancellationToken cancellationToken = default);

    /// <summary>Revoga o aceite vigente. Falso quando não havia nada vigente a revogar.</summary>
    Task<bool> RevokeUnsafeAsync(
        string tenantId, string projectId, DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}

public sealed record SandboxAttestationRecord(
    string TenantId,
    string AttemptId,
    string ProjectId,
    string Provider,
    string ProviderVersion,
    string SandboxIdentity,
    IReadOnlyList<string> Mounts,
    string NetworkPolicy,
    bool RootFilesystemReadOnly,
    bool WorktreeIsolated,
    bool EgressRestricted,
    bool ResourceLimitsApplied,
    bool Verified,
    string VerificationDetail,
    string ConfigurationHash,
    DateTimeOffset IssuedAt);

public sealed record UnsafeExecutionAcceptanceRecord(
    string TenantId,
    string ProjectId,
    string AcceptedByProfileId,
    string Reason,
    DateTimeOffset AcceptedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt = null);
