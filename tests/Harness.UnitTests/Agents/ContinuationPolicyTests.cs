using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A política de continuação é pura e Default-REFUSE: só autoriza retomar um artifact
/// arquivado quando identidade, repositório, veredito e escopo batem exatamente. Cada recusa
/// é um código tipado.
/// </summary>
public sealed class ContinuationPolicyTests
{
    private const string Repo = "/controlled/harness-poseidon-backend";

    private static ArchivedAttemptManifest Manifest() => new()
    {
        AttemptId = "01KY36MZMV5YFDHW9EWFZMK0CM",
        TenantId = "01KY000000000000000000TEN0",
        ProjectId = "01KY36JQ8A48Q2TVYJMA5Q1N4F",
        TaskId = "01KY36KYHVWB0ZYWCZQPBQZ1ZP",
        Role = "frontend-specialist",
        ActorAlias = "worker-codex-frontend",
        ControlledRepositoryRoot = Repo,
        SourceCommit = "6e4f713",
        PatchFileName = "attempt.patch",
        PatchSha256 = "abc",
        Verdict = "fail",
        ScopeClaims = ["frontend/**", "docs/frontend/**"],
        Findings =
        [
            new ArchivedAttemptFinding("P0", "suite-vermelha", "1 teste falhando"),
            new ArchivedAttemptFinding("P1", "estado-inicial-desonesto", "UI exibe concluído sem turno"),
        ],
        ArchivedAt = DateTimeOffset.UnixEpoch,
    };

    private static ContinuationPolicy.Request Request() => new(
        "frontend-specialist",
        "worker-codex-frontend",
        "01KY36JQ8A48Q2TVYJMA5Q1N4F",
        "01KY36KYHVWB0ZYWCZQPBQZ1ZP",
        Repo,
        ["frontend/**", "docs/frontend/**"]);

    [Fact]
    public void AValidContinuationIsAllowedAndTurnsFindingsIntoCriteria()
    {
        var decision = ContinuationPolicy.Evaluate(Manifest(), Request());

        Assert.True(decision.Allowed);
        Assert.Equal("continuation.allowed", decision.ReasonCode);
        Assert.Equal(2, decision.PriorFindings.Count);
        Assert.Contains("[P0 suite-vermelha]", decision.PriorFindings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAFailedAttemptCanBeContinued()
    {
        var decision = ContinuationPolicy.Evaluate(Manifest() with { Verdict = "pass" }, Request());
        Assert.False(decision.Allowed);
        Assert.Equal("continuation.verdict_not_fail", decision.ReasonCode);
    }

    [Theory]
    [InlineData("01KYDIFFERENTPROJECT0000000", null, null, null, "continuation.project_mismatch")]
    [InlineData(null, "01KYDIFFERENTTASK0000000000", null, null, "continuation.task_mismatch")]
    [InlineData(null, null, "backend-specialist", null, "continuation.role_mismatch")]
    [InlineData(null, null, null, "worker-claude-secondary", "continuation.actor_mismatch")]
    public void IdentityMismatchesAreRefusedWithTypedCodes(
        string? project, string? task, string? role, string? actor, string expected)
    {
        var manifest = Manifest() with
        {
            ProjectId = project ?? Manifest().ProjectId,
            TaskId = task ?? Manifest().TaskId,
            Role = role ?? Manifest().Role,
            ActorAlias = actor ?? Manifest().ActorAlias,
        };

        var decision = ContinuationPolicy.Evaluate(manifest, Request());
        Assert.False(decision.Allowed);
        Assert.Equal(expected, decision.ReasonCode);
    }

    [Fact]
    public void ADifferentRepositoryIsRefused()
    {
        var decision = ContinuationPolicy.Evaluate(
            Manifest() with { ControlledRepositoryRoot = "/somewhere/else" }, Request());
        Assert.False(decision.Allowed);
        Assert.Equal("continuation.repository_mismatch", decision.ReasonCode);
    }

    [Fact]
    public void AnArchivedScopeWiderThanTheRoleIsRefused()
    {
        // O arquivo reivindicava um claim que o papel não concede: escalar escopo por
        // continuação é exatamente o bypass que a política existe para barrar.
        var decision = ContinuationPolicy.Evaluate(
            Manifest() with { ScopeClaims = ["frontend/**", "src/**"] }, Request());
        Assert.False(decision.Allowed);
        Assert.Equal("continuation.scope_escalation", decision.ReasonCode);
    }
}
