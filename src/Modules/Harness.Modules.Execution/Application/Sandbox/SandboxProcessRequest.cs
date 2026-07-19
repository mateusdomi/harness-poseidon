namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxProcessRequest(
    string AttemptId,
    string ExecutionRoot,
    string WorktreePath,
    string AgentImageName,
    string ProxyImageName,
    string ProxyCommand,
    string ContainerExecutable,
    decimal CpuLimit,
    long MemoryBytes,
    long WritableDiskBytes,
    int PidsLimit);
