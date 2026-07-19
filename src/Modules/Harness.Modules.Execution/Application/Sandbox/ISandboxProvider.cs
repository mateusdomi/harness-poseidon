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
}
