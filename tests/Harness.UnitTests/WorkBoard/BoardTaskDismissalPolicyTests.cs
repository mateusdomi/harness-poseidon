using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.WorkBoard;

public sealed class BoardTaskDismissalPolicyTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-08-02T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("backlog", "ready", "tarefa", "user", "Não é mais necessário.", true)]
    [InlineData("review", "approved", "documento", "system", "document-gate:document.template_not_satisfied", true)]
    [InlineData("review", "approved", "documento", "user", "document-gate:document.template_not_satisfied", false)]
    [InlineData("review", "approved", "tarefa", "system", "document-gate:document.template_not_satisfied", false)]
    [InlineData("review", "approved", "documento", "system", "Quero cancelar.", false)]
    public void OnlyInactiveWorkOrTypedSystemDocumentSupersessionCanBeDismissed(
        string boardState,
        string internalState,
        string cardType,
        string actor,
        string reason,
        bool expected)
    {
        var task = new BoardTaskRecord(
            "tenant", "task", "project", "demand", "Documento", boardState,
            "low", null, null, 1, new(0, 0, 0), Now, Now, null, null, 1,
            internalState, "solicitation", "backing-demand", "3-Arquitetura", cardType);
        var command = new BoardTaskDismissCommand("tenant", "task", reason, actor, Now);

        Assert.Equal(expected, BoardTaskDismissalPolicy.MayDismiss(task, command));
    }
}
