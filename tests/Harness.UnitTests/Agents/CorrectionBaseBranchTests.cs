using Harness.Host.Agents;
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

    private static BoardAttemptRecord Attempt(string id, int number, string state) =>
        new("tenant", id, "task", number, state, "agent", Now, null, null, 0, 0, 0,
            [], null, null);
}
