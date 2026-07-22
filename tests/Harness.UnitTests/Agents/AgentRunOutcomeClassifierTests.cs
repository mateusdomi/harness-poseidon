using Harness.Modules.Agents.Application.Execution;
using Harness.Modules.Agents.Application.Execution.External;

namespace Harness.UnitTests.Agents;

/// <summary>
/// A base do retry inteligente: o desfecho de uma execução é classificado em ADIAR (cota),
/// ESCALAR (login/permanente) ou REPETIR (transitório). O GLM/Z.AI é instável e cai com
/// falhas transitórias, que devem ser candidatas a retry — não a escalonamento humano.
/// </summary>
public sealed class AgentRunOutcomeClassifierTests
{
    [Fact]
    public void ACompletedRunNeitherRetriesNorEscalates()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Completed, null);
        Assert.Equal(AgentRunOutcomeKind.Completed, outcome.Kind);
        Assert.False(outcome.ShouldRetry);
        Assert.False(outcome.NeedsHuman);
    }

    [Theory]
    [InlineData("executor.quota_exhausted")]
    [InlineData("quota.exhausted")]
    [InlineData("provider.rate_limit")]
    [InlineData("http_429_too_many_requests")]
    [InlineData("resource_exhausted")]
    public void AQuotaSignalWaitsForResetWithAConservativeCooldown(string failureCode)
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, failureCode);
        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.True(outcome.ShouldWaitForReset);
        Assert.False(outcome.ShouldRetry);
        Assert.Equal(AgentRunOutcomeClassifier.DefaultQuotaCooldown, outcome.SuggestedCooldown);
    }

    [Theory]
    [InlineData("executor.authentication_required")]
    [InlineData("Not logged in")]
    [InlineData("http_401_unauthorized")]
    public void AnAuthenticationSignalNeedsAHumanAndNeverRetriesAlone(string failureCode)
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, failureCode);
        Assert.Equal(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    [Theory]
    [InlineData(ExternalAgentRunStatus.TimedOut, null)]
    [InlineData(ExternalAgentRunStatus.Failed, "executor.exit_code_1")]
    [InlineData(ExternalAgentRunStatus.Failed, "connection reset by peer")]
    [InlineData(ExternalAgentRunStatus.Failed, "executor.no_output")]
    [InlineData(ExternalAgentRunStatus.Failed, "upstream 503 overloaded")]
    public void ATransientFailureIsACandidateForRetry(ExternalAgentRunStatus status, string? failureCode)
    {
        // O GLM instável cai justamente aqui — retry com backoff, não escalonamento.
        var outcome = AgentRunOutcomeClassifier.Classify(status, failureCode);
        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
        Assert.True(outcome.ShouldRetry);
        Assert.False(outcome.NeedsHuman);
    }

    [Fact]
    public void AnUnrecognizedFailureIsPermanentAndEscalatesInsteadOfRetryingBlindly()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Failed, "critic.pass_contradicted_by_findings");
        Assert.Equal(AgentRunOutcomeKind.Permanent, outcome.Kind);
        Assert.True(outcome.NeedsHuman);
        Assert.False(outcome.ShouldRetry);
    }

    [Fact]
    public void ACancelledRunIsNeverRetried()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(ExternalAgentRunStatus.Cancelled, "executor.cancelled");
        Assert.Equal(AgentRunOutcomeKind.Cancelled, outcome.Kind);
        Assert.False(outcome.ShouldRetry);
    }
}
