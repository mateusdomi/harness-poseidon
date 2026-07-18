namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxRunResult(
    string ContainerName,
    int ExitCode,
    string StandardOutput,
    decimal CpuLimit,
    long MemoryBytes,
    long WritableDiskBytes,
    int PidsLimit,
    bool RootFilesystemReadOnly,
    string NetworkMode);
