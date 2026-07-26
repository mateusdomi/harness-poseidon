using Harness.Host.Observability;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.CodexCli;
using Harness.Modules.Execution.Application.Sandbox;

namespace Harness.Host.Execution;

public sealed class CodexCliSandboxExecutorFactory(IsolatedExecutionOptions options)
    : ISandboxAgentExecutorFactory
{
    private readonly IsolatedExecutionOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);
        var proof = new CodexCliExternalSandboxProof(
            plan.RootFilesystemReadOnly,
            plan.WorktreeIsolated,
            plan.EgressRestricted,
            plan.ResourceLimitsApplied);
        var stateDirectory = Path.Combine(
            command.ControlledRoot,
            ".harness-codex-state",
            command.AttemptId);
        Directory.CreateDirectory(stateDirectory);
        return new InstrumentedAgentExecutor(
            new CodexCliAgentExecutor(
                proof,
                _ => new CodexCliAppServerOptions(
                    plan.HostExecutablePath,
                    command.ControlledRoot,
                    command.WorktreePath,
                    stateDirectory,
                    _options.HeartbeatInterval,
                    plan.ExecutablePrefixArguments,
                    plan.AgentWorkingDirectory)));
    }
}
