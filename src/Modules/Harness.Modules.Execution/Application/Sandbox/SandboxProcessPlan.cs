namespace Harness.Modules.Execution.Application.Sandbox;

public sealed record SandboxProcessPlan(
    string HostExecutablePath,
    IReadOnlyList<string> ExecutablePrefixArguments,
    string AgentWorkingDirectory,
    bool RootFilesystemReadOnly,
    bool WorktreeIsolated,
    bool EgressRestricted,
    bool ResourceLimitsApplied);
