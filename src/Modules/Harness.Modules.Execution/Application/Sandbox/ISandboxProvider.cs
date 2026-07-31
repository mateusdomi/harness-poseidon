namespace Harness.Modules.Execution.Application.Sandbox;

public interface ISandboxProvider
{
    Task<ISandboxProcessSession> OpenProcessSessionAsync(
        SandboxProcessRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxRunResult> RunAsync(
        SandboxRunRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxResourceInventory> DetectResourcesAsync(
        string attemptId,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(
        string attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fase 0B1 (BR-002): emite a ATTESTATION da sandbox desta tentativa. Quem afirma que existe
    /// fronteira é quem a constrói — não o control plane, que antes assumia o valor por literal.
    /// Um provider indisponível deve devolver uma attestation NÃO verificada, com o motivo; jamais
    /// lançar e deixar o chamador seguir sem saber.
    /// </summary>
    Task<SandboxAttestation> AttestAsync(
        SandboxAttestationRequest request,
        CancellationToken cancellationToken = default);
}
