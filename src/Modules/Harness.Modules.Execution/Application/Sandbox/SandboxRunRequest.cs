namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxRunRequest(
    string AttemptId,
    string ExecutionRoot,
    string WorktreePath,
    string ImageName,
    decimal CpuLimit,
    long MemoryBytes,
    long WritableDiskBytes,
    int PidsLimit);
