using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Agents;

/// <summary>
/// F-06 — a classificação MAST do modo de falha deve usar reason codes canônicos, não
/// <c>Contains("scope")</c> ou <c>Contains("tool")</c> sobre texto livre.
/// </summary>
public sealed class AgentRunFailureModeClassificationTests
{
    [Theory]
    [InlineData("agent_path_scope_denied", MastTaxonomy.DisobeyRoleSpecification)]
    [InlineData("executor.tool_permission_denied", MastTaxonomy.DisobeyTaskSpecification)]
    [InlineData("run.timeout", MastTaxonomy.PrematureTermination)]
    [InlineData("run.quota_exhausted", MastTaxonomy.PrematureTermination)]
    [InlineData("run.authentication_required", MastTaxonomy.PrematureTermination)]
    [InlineData("executor.exit_code_1", MastTaxonomy.PrematureTermination)]
    [InlineData("scope.expansion_granted", MastTaxonomy.PrematureTermination)]
    [InlineData("continuation.scope_escalation", MastTaxonomy.PrematureTermination)]
    [InlineData("tool.statusChanged", MastTaxonomy.PrematureTermination)]
    [InlineData(null, MastTaxonomy.PrematureTermination)]
    public void MapsCanonicalFailureCodesToMastModes(string? failureCode, string expectedMode)
    {
        Assert.Equal(expectedMode, AgentRunOrchestrator.ClassifyFailureMode(failureCode));
    }
}
