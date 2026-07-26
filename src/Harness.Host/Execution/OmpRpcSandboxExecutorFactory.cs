using Harness.Host.Observability;
using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Infrastructure.OmpRpc;
using Harness.Modules.Execution.Application.Sandbox;

namespace Harness.Host.Execution;

public sealed class OmpRpcSandboxExecutorFactory(
    OmpRpcAgentExecutorOptions options) : ISandboxAgentExecutorFactory
{
    private readonly OmpRpcAgentExecutorOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public IAgentExecutor Create(SandboxProcessPlan plan, StartIsolatedExecutionCommand command)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);
        if (!plan.RootFilesystemReadOnly || !plan.WorktreeIsolated ||
            !plan.EgressRestricted || !plan.ResourceLimitsApplied)
        {
            throw new InvalidOperationException("OMP RPC requires the full Docker sandbox proof.");
        }

        return new InstrumentedAgentExecutor(
            new OmpRpcAgentExecutor(_options with
            {
                Enabled = true,
                Executable = plan.HostExecutablePath,
                PrefixArguments = plan.ExecutablePrefixArguments,
                ProcessWorkingDirectory = command.ControlledRoot,
                AgentWorkingDirectory = plan.AgentWorkingDirectory,
            }));
    }
}
