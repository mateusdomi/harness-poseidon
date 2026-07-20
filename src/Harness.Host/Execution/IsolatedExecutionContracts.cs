using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Execution.Application.Sandbox;
using Harness.Modules.Governance.Coordination;
using Harness.Persistence.Abstractions.AttemptWorkspaces;

namespace Harness.Host.Execution;

public enum IsolatedExecutionStatus
{
    Completed,
    Failed,
    ScopeConflict,
    Rejected,
}

public sealed record StartIsolatedExecutionCommand
{
    public AgentPathScopeKind PathScopeKind { get; init; } = AgentPathScopeKind.Backend;

    public bool EnforcePoseidonPathPolicy { get; init; }

    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    public required string ConversationId { get; init; }

    public required string AgentId { get; init; }

    public required string Instruction { get; init; }

    public required string StatusDigestJson { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string ControlledRoot { get; init; }

    public required string BaseReference { get; init; }

    public required string BranchName { get; init; }

    public required string WorktreePath { get; init; }

    public required IReadOnlyList<string> ScopeClaims { get; init; }

    public required string Owner { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required string IdempotencyKey { get; init; }
}

public sealed record IsolatedExecutionResult(
    IsolatedExecutionStatus Status,
    AttemptWorkspaceSnapshot? Workspace,
    IReadOnlyList<AttemptScopeConflict> Conflicts,
    AgentExecutionResult? Execution,
    string? FinalError);

public interface ISandboxAgentExecutorFactory
{
    IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command);
}

public sealed record IsolatedExecutionOptions
{
    public required string AgentImageName { get; init; }

    public required string ProxyImageName { get; init; }

    public required string ProxyCommand { get; init; }

    public required string ContainerExecutable { get; init; }

    public decimal CpuLimit { get; init; } = 1.0m;

    public long MemoryBytes { get; init; } = 1L << 30;

    public long WritableDiskBytes { get; init; } = 256L << 20;

    public int PidsLimit { get; init; } = 256;

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
}
