using Harness.Modules.Tools.Application;
using Harness.Modules.Tools.Domain;

namespace Harness.UnitTests.Tools;

public sealed class ToolExecutionPolicyTests
{
    private const string ToolId = "01ARZ3NDEKTSV4RRFFQ69G5FD1";
    private static readonly ToolPolicyDescriptor Descriptor = new(ToolId, true, ToolRiskTier.Critical, "{}", "{}");

    [Theory]
    [InlineData(false, true, true, ToolRiskTier.Low, ToolRiskTier.Low, "tool_disabled")]
    [InlineData(true, false, true, ToolRiskTier.Low, ToolRiskTier.Low, "tool_not_allowlisted")]
    [InlineData(true, true, true, ToolRiskTier.High, ToolRiskTier.Critical, "task_risk_exceeded")]
    public void DeniesDisabledUnlistedAndRiskEscalation(bool enabled, bool allowlisted, bool sandbox,
        ToolRiskTier taskRisk, ToolRiskTier invocationRisk, string code)
    {
        var descriptor = Descriptor with { Enabled = enabled };
        var context = new ToolPolicyContext("Implementation", taskRisk,
            allowlisted ? new HashSet<string> { ToolId } : new HashSet<string>(), sandbox, false);
        Assert.Equal(code, ToolExecutionPolicy.Evaluate(new(descriptor, context, invocationRisk)).Code);
    }

    [Fact]
    public async Task TypedDecoratorNeverInvokesInnerWhenPolicyDeniesAndAllowsSafeExecution()
    {
        var executor = new RecordingExecutor(); var sut = new PolicyCheckedToolExecutor<string, string>(executor);
        var denied = new PolicyCheckedToolInvocation<string>("danger", Descriptor,
            new("Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId }, false, false), ToolRiskTier.High);
        var error = await Assert.ThrowsAsync<ToolPolicyDeniedException>(() => sut.ExecuteAsync(denied));
        Assert.Equal("sandbox_required", error.Decision.Code); Assert.Equal(0, executor.CallCount);
        var allowed = denied with { Input = "safe", Context = denied.Context with { SandboxActive = true } };
        Assert.Equal("SAFE", await sut.ExecuteAsync(allowed)); Assert.Equal(1, executor.CallCount);
    }

    private sealed class RecordingExecutor : IToolExecutor<string, string>
    {
        public string ToolId => ToolExecutionPolicyTests.ToolId;
        public int CallCount { get; private set; }
        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(input.ToUpperInvariant()); }
    }
}
