using Harness.Host.Agents;

namespace Harness.UnitTests.Agents;

/// <summary>
/// F-06 — os reason codes de infraestrutura devem ser reconhecidos por igualdade canônica, não por
/// <c>Contains</c> sobre texto livre. Uma mensagem de erro que contenha a palavra "quota" ou
/// "authentication" mas descreva uma falha real de trabalho não pode ser classificada como
/// infraestrutura.
/// </summary>
public sealed class CardCircuitBreakerInfrastructureReasonTests
{
    [Theory]
    [InlineData("attempt.interrupted_by_host_shutdown")]
    [InlineData("attempt.orphaned_by_host_restart")]
    [InlineData("run.quota_exhausted")]
    [InlineData("executor.quota_exhausted")]
    [InlineData("run.authentication_required")]
    [InlineData("executor.authentication_required")]
    [InlineData("run.account_model_unsupported")]
    [InlineData("executor.account_model_unsupported")]
    [InlineData("run.cancelled")]
    [InlineData("TaskCanceledException")]
    [InlineData("OperationCanceledException")]
    [InlineData("chief.dispatch_rejected: account_busy")]
    [InlineData("chief.dispatch_rejected: claim_conflict")]
    [InlineData("profile.locked")]
    [InlineData("profile.concurrency_exhausted")]
    public void RecognizesCanonicalInfrastructureReasons(string reason)
    {
        Assert.True(CardCircuitBreakerService.IsInfrastructureFailure(reason));
    }

    [Theory]
    [InlineData("executor.exit_code_1")]
    [InlineData("executor.turn_failed")]
    [InlineData("run.permanent_failure")]
    [InlineData("review.rejected")]
    [InlineData("scope.expansion_granted")]
    [InlineData("continuation.scope_escalation")]
    [InlineData("the provider returned a quota error while processing the file")]
    [InlineData("authentication_required is the next step in the flow")]
    [InlineData("account_model_unsupported message embedded in diagnostic")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsFreeTextOrNonInfrastructureReasons(string? reason)
    {
        Assert.False(CardCircuitBreakerService.IsInfrastructureFailure(reason));
    }
}
