using Harness.Host.Agents;
using Harness.Modules.Agents.Application.Execution.External;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Agents;

public sealed class CorrectionBaseBranchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-08-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void CorrectionStartsFromTheMostRecentRejectedAttemptBranch()
    {
        var attempts = new[]
        {
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAA", 1, "rejected"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAB", 2, "rejected"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAC", 3, "running"),
        };

        Assert.Equal(
            "task/agent-run-01arz3ndektsv4rrffq69g5fab",
            ChiefBacklogLoopService.CorrectionBaseBranch(attempts));
    }

    [Fact]
    public void FirstAttemptStartsFromTheRepositoryHead()
    {
        Assert.Null(ChiefBacklogLoopService.CorrectionBaseBranch(
            [Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAA", 1, "running")]));
    }

    [Fact]
    public void UnresolvedDeliveryPlaceholdersFailBeforeBehavioralReview()
    {
        var findings = ChiefBacklogLoopService.ForbiddenDeliveryPlaceholders(
            """
            --- a/doc.md
            +++ b/doc.md
            -Commit: PENDING_PUB_SHA
            +Commit: PENDING_PUB_SHA
            +Texto legítimo sem marcador
            """);

        Assert.Equal(["Commit: PENDING_PUB_SHA"], findings);
    }

    [Fact]
    public void RemovedPlaceholdersDoNotBlockTheCorrectedDelivery()
    {
        Assert.Empty(ChiefBacklogLoopService.ForbiddenDeliveryPlaceholders(
            "-TODO: preencher\n+Commit: 0123456789abcdef"));
    }

    /// <summary>
    /// A primeira entrega de CÓDIGO da operação (tentativa 01KZ51F6KBDCTF9SKT5520HVA0) foi barrada
    /// por estas duas linhas: `metodo:` contém `TODO:` como substring. O produto escreve em
    /// português, então o falso-positivo não é raro — é o caso comum.
    /// </summary>
    [Fact]
    public void PortugueseIdentifiersAreNotMistakenForPlaceholders()
    {
        Assert.Empty(ChiefBacklogLoopService.ForbiddenDeliveryPlaceholders(
            """
            +        chamadas.push({ url, metodo: opcoes?.method });
            +    deepEqual(chamadas, [{ url: 'https://x/devolucao', metodo: 'POST' }]);
            +const substituicaoDeMetodo = { metodo: 'GET' };
            """));
    }

    [Fact]
    public void RealPlaceholdersStillFailEvenNextToPunctuation()
    {
        var findings = ChiefBacklogLoopService.ForbiddenDeliveryPlaceholders(
            """
            +// TODO: implementar a devolução
            +const sha = '<commit-sha>';
            +export const chave = 'REPLACE_ME';
            +const replacement = 'REPLACE_MENT';
            """);

        Assert.Equal(
            [
                "// TODO: implementar a devolução",
                "const sha = '<commit-sha>';",
                "export const chave = 'REPLACE_ME';",
            ],
            findings);
    }

    /// <summary>
    /// O gate de placeholder emitia `critic.delivery_placeholder`, que não é um motivo aplicável:
    /// `ApplyReviewVerdictAsync` devolvia `false` por definição, o card adiava quatro vezes e
    /// escalava como "revisão indisponível" — nunca como a reprovação que o gate tinha apurado.
    /// </summary>
    [Fact]
    public void DeterministicRejectionsAreAppliableVerdictsAndNotInfrastructureFailures()
    {
        Assert.True(ChiefBacklogLoopService.IsAppliableReviewReason(
            ChiefBacklogLoopService.DeterministicRejectionReasonCode));
        Assert.False(ChiefBacklogLoopService.IsAppliableReviewReason("critic.delivery_placeholder"));
    }

    [Fact]
    public void DispatchDeferralsDoNotConsumeTheCardRoundBudget()
    {
        var attempts = new[]
        {
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAA", 1, "cancelled"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAB", 2, "queued"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAC", 3, "completed"),
        };

        Assert.Equal(1, ChiefBacklogLoopService.CountSpentRounds(attempts));
    }

    [Fact]
    public void ExecutedFailuresAndActiveRunsConsumeTheCardRoundBudget()
    {
        var attempts = new[]
        {
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAA", 1, "failed"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAB", 2, "running"),
            Attempt("01ARZ3NDEKTSV4RRFFQ69G5FAC", 3, "completed"),
        };

        Assert.Equal(3, ChiefBacklogLoopService.CountSpentRounds(attempts));
    }

    [Theory]
    [InlineData(ExternalAgentRunStatus.Failed, "critic.pass_contradicted_by_findings", true)]
    [InlineData(ExternalAgentRunStatus.Failed, "connection reset by peer", false)]
    [InlineData(ExternalAgentRunStatus.TimedOut, "executor.timeout", false)]
    [InlineData(ExternalAgentRunStatus.Cancelled, "executor.cancelled", false)]
    public void OnlyPermanentExecutedFailuresConsumeARound(
        ExternalAgentRunStatus externalStatus,
        string failureCode,
        bool expected)
    {
        var execution = new ExternalAgentRunResult(
            "executor", "account", null, externalStatus, string.Empty, [], null, null,
            failureCode, 100);
        var snapshot = new AgentRunSnapshot(
            "run", "attempt", "account", "role", "executor", AgentRunStatus.Failed,
            null, [], execution, null, null, null, null, null, failureCode);

        Assert.Equal(expected,
            ChiefBacklogLoopService.CountsFailedRunTowardRoundBudget(snapshot));
    }

    [Fact]
    public void OrchestratorFailureWithoutAnExecutorResultDoesNotConsumeARound()
    {
        var snapshot = new AgentRunSnapshot(
            "run", "attempt", "account", "role", "executor", AgentRunStatus.Failed,
            null, [], null, null, null, null, null, null, "IOException");

        Assert.False(ChiefBacklogLoopService.CountsFailedRunTowardRoundBudget(snapshot));
    }

    [Theory]
    [InlineData(AgentRunStatus.Failed, true)]
    [InlineData(AgentRunStatus.Cancelled, true)]
    [InlineData(AgentRunStatus.Completed, false)]
    [InlineData(AgentRunStatus.Running, false)]
    public void TerminalFailureGetsABackoffBeforeRedispatch(
        AgentRunStatus status,
        bool expected)
    {
        var snapshot = new AgentRunSnapshot(
            "run", "attempt", "account", "role", "executor", status,
            null, [], null, null, null, null, null, null, null);

        var delay = ChiefBacklogLoopService.RetryDelayAfterRun(snapshot);

        Assert.Equal(expected, delay is not null);
        if (expected)
        {
            Assert.True(delay >= TimeSpan.FromMinutes(1));
        }
    }

    private static BoardAttemptRecord Attempt(string id, int number, string state) =>
        new("tenant", id, "task", number, state, "agent", Now, null, null, 0, 0, 0,
            [], null, null);
}
