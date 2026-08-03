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
            allowlisted ? new HashSet<string> { ToolId } : new HashSet<string>(), sandbox);
        Assert.Equal(code, ToolExecutionPolicy.Evaluate(new(descriptor, context, invocationRisk)).Code);
    }

    [Fact]
    public async Task TypedDecoratorNeverInvokesInnerWhenPolicyDeniesAndAllowsSafeExecution()
    {
        var executor = new RecordingExecutor();
        var audit = new RecordingCapabilityAuditSink();
        var pep = new SecurityPolicyEnforcementPoint(audit);
        var token = pep.Issue(Grant());
        var sut = new PolicyCheckedToolExecutor<string, string>(executor, pep);
        var denied = new PolicyCheckedToolInvocation<string>("danger", Descriptor,
            new("Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId }, false),
            ToolRiskTier.High,
            token,
            Authorization());
        var error = await Assert.ThrowsAsync<ToolPolicyDeniedException>(() => sut.ExecuteAsync(denied));
        Assert.Equal("sandbox_required", error.Decision.Code); Assert.Equal(0, executor.CallCount);
        var allowed = denied with { Input = "safe", Context = denied.Context with { SandboxActive = true } };
        Assert.Equal("SAFE", await sut.ExecuteAsync(allowed)); Assert.Equal(1, executor.CallCount);
        Assert.All(audit.Records, record => Assert.True(record.Allowed));
    }

    [Fact]
    public async Task TypedDecoratorNeverInvokesInnerWhenCatalogMarksToolDisabled()
    {
        var executor = new RecordingExecutor();
        var pep = new SecurityPolicyEnforcementPoint(new RecordingCapabilityAuditSink());
        var token = pep.Issue(Grant());
        var sut = new PolicyCheckedToolExecutor<string, string>(executor, pep);
        var invocation = new PolicyCheckedToolInvocation<string>(
            "disabled",
            Descriptor with { Enabled = false },
            new("Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId }, true),
            ToolRiskTier.High,
            token,
            Authorization());

        var error = await Assert.ThrowsAsync<ToolPolicyDeniedException>(
            () => sut.ExecuteAsync(invocation));

        Assert.Equal("tool_disabled", error.Decision.Code);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task TypedDecoratorDeniesChiefExecutionBeforeInvokingToolAndAuditsDecision()
    {
        var executor = new RecordingExecutor();
        var audit = new RecordingCapabilityAuditSink();
        var pep = new SecurityPolicyEnforcementPoint(audit);
        var token = pep.Issue(Grant() with { ActorKind = CapabilityActorKind.Chief, ActorId = "bruna" });
        var sut = new PolicyCheckedToolExecutor<string, string>(executor, pep);
        var invocation = new PolicyCheckedToolInvocation<string>(
            "unsafe",
            Descriptor,
            new("Implementation", ToolRiskTier.Low, new HashSet<string> { ToolId }, true),
            ToolRiskTier.Low,
            token,
            Authorization() with { ActorKind = CapabilityActorKind.Chief, ActorId = "bruna" });

        var error = await Assert.ThrowsAsync<CapabilityDeniedException>(() => sut.ExecuteAsync(invocation));

        Assert.Equal("chief_execution_denied", error.Decision.Code);
        Assert.Equal(0, executor.CallCount);
        Assert.Collection(audit.Records, record =>
        {
            Assert.False(record.Allowed);
            Assert.Equal("chief_execution_denied", record.Code);
            Assert.Equal(token.Id, record.CapabilityId);
        });
    }

    private static CapabilityGrantRequest Grant() => new(
        CapabilityActorKind.Specialist,
        "agent-1",
        "tenant-1",
        "project-1",
        "card-1",
        "attempt-1",
        CapabilityOperation.ToolExecution,
        [ToolId],
        ["workspace"],
        ["src/Modules/Harness.Modules.Tools/**"],
        DateTimeOffset.UtcNow.AddMinutes(5),
        7);

    private static CapabilityAuthorizationRequest Authorization() => new(
        CapabilityActorKind.Specialist,
        "agent-1",
        "tenant-1",
        "project-1",
        "card-1",
        "attempt-1",
        CapabilityOperation.ToolExecution,
        ToolId,
        "workspace",
        "src/Modules/Harness.Modules.Tools/Application/IToolExecutor.cs",
        7);

    private sealed class RecordingExecutor : IToolExecutor<string, string>
    {
        public string ToolId => ToolExecutionPolicyTests.ToolId;
        public int CallCount { get; private set; }
        public Task<string> ExecuteAsync(string input, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(input.ToUpperInvariant()); }
    }

    private sealed class RecordingCapabilityAuditSink : ICapabilityDecisionAuditSink
    {
        public List<CapabilityDecisionAuditRecord> Records { get; } = [];

        public ValueTask RecordAsync(
            CapabilityDecisionAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A exceção que o proprietário assinou em 03/08/2026, com causa medida: as contas do
    /// Claude Code guardam credencial no Keychain do macOS, inexistente dentro do contêiner —
    /// a MESMA conta responde no host e responde `Invalid API key` lá dentro. O isolamento não
    /// continha risco: cegava as únicas contas com cota.
    ///
    /// A exceção é da INSTALAÇÃO e não do card, e é isso que a torna aceitável: aparece na
    /// configuração, vale para todos e sai com código próprio no registro.
    /// </summary>
    [Fact]
    public void TheOperatorCanDeclareThatThisInstallationRunsWithoutAContainer()
    {
        var decision = ToolExecutionPolicy.Evaluate(new ToolInvocationPolicyRequest(
            new ToolPolicyDescriptor(ToolId, true, ToolRiskTier.Critical, "{}", "{}"),
            new ToolPolicyContext(
                "Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId },
                SandboxActive: false, UncontainedExecutionAcknowledged: true),
            ToolRiskTier.Critical));

        Assert.True(decision.Allowed);

        // Código próprio: um `allowed` indistinguível esconderia quantas execuções
        // aconteceram sem contenção.
        Assert.Equal("allowed_uncontained", decision.Code);
    }

    /// <summary>Sem a declaração, nada muda: o padrão continua exigindo a sandbox.</summary>
    [Fact]
    public void WithoutTheDeclarationTheSandboxIsStillMandatory()
    {
        var decision = ToolExecutionPolicy.Evaluate(new ToolInvocationPolicyRequest(
            new ToolPolicyDescriptor(ToolId, true, ToolRiskTier.Critical, "{}", "{}"),
            new ToolPolicyContext(
                "Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId },
                SandboxActive: false),
            ToolRiskTier.Critical));

        Assert.False(decision.Allowed);
        Assert.Equal("sandbox_required", decision.Code);
    }

    /// <summary>
    /// A declaração dispensa a CONTENÇÃO, não a política: ferramenta fora da allowlist da
    /// persona continua negada, com contêiner ou sem ele.
    /// </summary>
    [Fact]
    public void TheDeclarationDoesNotOpenTheDoorToToolsOutsideTheAllowlist()
    {
        var decision = ToolExecutionPolicy.Evaluate(new ToolInvocationPolicyRequest(
            new ToolPolicyDescriptor("outra-ferramenta", true, ToolRiskTier.Critical, "{}", "{}"),
            new ToolPolicyContext(
                "Implementation", ToolRiskTier.Critical, new HashSet<string> { ToolId },
                SandboxActive: false, UncontainedExecutionAcknowledged: true),
            ToolRiskTier.Critical));

        Assert.False(decision.Allowed);
        Assert.Equal("tool_not_allowlisted", decision.Code);
    }
}
