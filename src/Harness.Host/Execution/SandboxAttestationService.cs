using Harness.Host.Observability;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Persistence.Abstractions.Execution;
using Harness.SharedKernel.Time;

namespace Harness.Host.Execution;

/// <summary>
/// Fase 0B1 (BR-002/BR-013): a única fonte de <c>SandboxActive</c>.
///
/// O valor era literal <c>true</c> no orquestrador — inclusive com o modo isolado desligado. A
/// política de ferramentas lia isso e liberava execução de risco crítico acreditando existir uma
/// fronteira que não existia. Agora o valor vem de uma attestation emitida pelo provider REAL da
/// tentativa, persistida e auditável.
///
/// Fail-closed em todas as bordas: sem provider, sem attestation, attestation não verificada ou
/// attestation de OUTRA tentativa, o resultado é "sem sandbox" — nunca um fallback silencioso para
/// o host. E, desde a decisão do proprietário de 31/07/2026, "sem sandbox" significa NÃO EXECUTA:
/// não há mais aceite de risco que dispense o contêiner.
/// </summary>
public sealed class SandboxAttestationService(
    ISandboxAttestationStore store,
    IClock clock,
    IsolatedExecutionSettings settings,
    ILogger<SandboxAttestationService> logger,
    ISandboxProvider? provider = null)
{
    private static readonly Action<ILogger, string, string, Exception?> AttestationLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2120, nameof(AttestationLog)),
            "Sandbox attestation for attempt {AttemptId} resolved as {Outcome}.");

    private readonly ISandboxAttestationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IsolatedExecutionSettings _settings =
        settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ILogger<SandboxAttestationService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ISandboxProvider? _provider = provider;

    /// <summary>
    /// Emite (uma vez) e persiste a attestation da tentativa. Chamado antes de qualquer decisão de
    /// política — a evidência precisa existir antes da autorização, não depois.
    /// </summary>
    public async Task<SandboxAttestationRecord> AttestAsync(
        string tenantId, string projectId, string attemptId,
        string? resourceSelector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        var now = _clock.UtcNow;
        var attestation = _provider is null || _settings.Mode == IsolatedExecutionMode.Disabled
            ? SandboxAttestation.Absent(
                tenantId, projectId, attemptId,
                "Isolated execution is disabled on this host; no sandbox was created.", now)
            : await _provider.AttestAsync(
                new SandboxAttestationRequest(tenantId, projectId, attemptId, now, resourceSelector),
                cancellationToken);

        var record = await _store.SaveAsync(ToRecord(attestation), cancellationToken);
        AttestationLog(
            _logger, attemptId, record.Verified ? "verified" : $"unverified:{record.Provider}", null);
        PoseidonTelemetry.RecordSandboxAttestation(record.Provider, record.Verified);
        return record;
    }

    /// <summary>
    /// A sandbox está EFETIVAMENTE ativa para esta tentativa? Só devolve verdadeiro quando existe
    /// attestation verificada, emitida para ESTA tentativa, com as quatro fronteiras de pé. Uma
    /// attestation de outro attempt não vale: bastaria uma execução isolada no passado para liberar
    /// todas as seguintes.
    /// </summary>
    public async Task<bool> IsSandboxActiveAsync(
        string tenantId, string attemptId, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetAsync(tenantId, attemptId, cancellationToken);
        return record is not null &&
            string.Equals(record.AttemptId, attemptId, StringComparison.Ordinal) &&
            record.Verified &&
            !string.Equals(record.Provider, SandboxAttestation.NoneProvider, StringComparison.Ordinal) &&
            record.RootFilesystemReadOnly &&
            record.WorktreeIsolated &&
            record.EgressRestricted &&
            record.ResourceLimitsApplied;
    }

    private static SandboxAttestationRecord ToRecord(SandboxAttestation attestation) => new(
        attestation.TenantId,
        attestation.AttemptId,
        attestation.ProjectId,
        attestation.Provider,
        attestation.ProviderVersion,
        attestation.SandboxIdentity,
        attestation.Mounts,
        attestation.NetworkPolicy,
        attestation.RootFilesystemReadOnly,
        attestation.WorktreeIsolated,
        attestation.EgressRestricted,
        attestation.ResourceLimitsApplied,
        attestation.IsEffective,
        attestation.VerificationDetail,
        attestation.ConfigurationHash,
        attestation.IssuedAt);
}
