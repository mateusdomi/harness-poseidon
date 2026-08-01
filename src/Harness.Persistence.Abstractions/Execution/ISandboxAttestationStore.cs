namespace Harness.Persistence.Abstractions.Execution;

/// <summary>
/// Fase 0B1 (BR-002/BR-013): guarda a PROVA de contenção da tentativa. É fato auditável, nunca
/// inferido em tempo de leitura. O aceite de modo inseguro foi extinto em 31/07/2026 — o contêiner
/// é pré-requisito nos dois modos, sem exceção temporal.
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
