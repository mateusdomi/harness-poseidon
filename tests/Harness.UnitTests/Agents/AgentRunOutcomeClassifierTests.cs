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

    /// <summary>
    /// Cota esgotada que a CLI não traduz em código estruturado.
    ///
    /// Observado no E2E de empréstimos: o GLM/Z.AI respondeu
    /// `429 rate_limit_error [1308] Usage limit reached for 5 hour`, a CLI imprimiu isso no erro
    /// padrão e saiu com código 1. Só `executor.exit_code_1` chegava aqui — e ele casa com o sinal
    /// `exit_code` da lista TRANSITÓRIA. A conta voltava de um cooldown curto, era reeleita,
    /// queimava mais ~200s sem produzir um token e derrubava outra rodada do card. Três dessas e
    /// um card saudável morria por um problema que era da conta.
    /// </summary>
    [Fact]
    public void QuotaExhaustionIsRecognizedFromTheExecutorDiagnosticWhenTheCodeOnlySaysExitCode()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"code\":\"1308\"," +
            "\"message\":\"[1308][Usage limit reached for 5 hour. Your limit will reset at " +
            "2026-08-03 06:00:40]\"}}");

        Assert.Equal(AgentRunOutcomeKind.QuotaExhausted, outcome.Kind);
        Assert.True(outcome.ShouldWaitForReset);
        Assert.NotNull(outcome.SuggestedCooldown);
    }

    /// <summary>
    /// Sem sinal de cota no diagnóstico, um exit code continua sendo instabilidade — o diagnóstico
    /// amplia o reconhecimento de cota, não reclassifica tudo.
    /// </summary>
    [Fact]
    public void AnOrdinaryExitCodeStaysTransientEvenWithADiagnostic()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "TypeError: cannot read property 'map' of undefined");

        Assert.Equal(AgentRunOutcomeKind.Transient, outcome.Kind);
    }

    /// <summary>
    /// Autenticação NÃO é inferida de texto solto: ela exige ação humana e não se recupera
    /// sozinha, então um falso positivo vindo do erro padrão pararia a conta até alguém intervir.
    /// O diagnóstico serve só para cota.
    /// </summary>
    [Fact]
    public void AuthenticationIsNeverInferredFromTheDiagnostic()
    {
        var outcome = AgentRunOutcomeClassifier.Classify(
            ExternalAgentRunStatus.Failed,
            "executor.exit_code_1",
            "warning: unauthorized access to /tmp/cache ignored");

        Assert.NotEqual(AgentRunOutcomeKind.AuthenticationRequired, outcome.Kind);
    }
}
