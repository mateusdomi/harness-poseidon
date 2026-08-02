using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.IntegrationTests.Agents;

public sealed class AgentRunFailureDiagnosticTests
{
    [Fact]
    public void FinalErrorCombinesStableCodeAndRedactedDiagnostic()
    {
        var result = new ExternalAgentRunResult(
            "claude-code", "worker", null, ExternalAgentRunStatus.Failed,
            string.Empty, [], null, 1, "executor.exit_code_1", 125)
        {
            FailureDiagnostic = "provider connection reset\nretry exhausted",
        };

        var finalError = AgentRunOrchestrator.ComposeExecutionFailure(result);

        Assert.Equal(
            "executor.exit_code_1: provider connection reset retry exhausted",
            finalError);
    }

    [Fact]
    public void FinalErrorFallsBackToStableFailureCode()
    {
        var result = new ExternalAgentRunResult(
            "claude-code", "worker", null, ExternalAgentRunStatus.Failed,
            string.Empty, [], null, null, null, 125);

        Assert.Equal("executor.failed", AgentRunOrchestrator.ComposeExecutionFailure(result));
    }
}
