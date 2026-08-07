using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.WorkBoard;

/// <summary>
/// Decorator do WorkChain que serializa a MUTAÇÃO DE MERGE pela fila única do
/// <see cref="ISerializedMergeCoordinator"/> (Fase 10): a integração em `develop` é o único
/// ponto do fluxo que não pode acontecer em paralelo, então todo merge — de qualquer caminho
/// do Host — entra na mesma fila, e a contenção é MEDIDA (fila, espera, razão de conflito),
/// que é o gatilho decidido em arquitetura para reavaliar infraestrutura de fila externa.
///
/// Todas as demais operações passam direto: leitura e mutações não-merge já são seguras por
/// OCC/fencing no store subjacente.
/// </summary>
public sealed class SerializedMergeWorkChainStore(
    IWorkChainStore inner,
    ISerializedMergeCoordinator coordinator) : IWorkChainStore
{
    private readonly IWorkChainStore _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly ISerializedMergeCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<WorkChainMutationReceipt> MergeApprovedTaskAsync(
        WorkTaskMergeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _coordinator.ExecuteAsync(
            command.TaskId,
            token => _inner.MergeApprovedTaskAsync(command, token),
            cancellationToken);
    }

    public Task<WorkChainCreateReceipt> CreateAsync(
        WorkChainCreateCommand command, CancellationToken cancellationToken = default) =>
        _inner.CreateAsync(command, cancellationToken);

    public Task<WorkChainSnapshot?> ReadAsync(
        string tenantId, string solicitationId, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(tenantId, solicitationId, cancellationToken);

    public Task<WorkChainAggregateSnapshot?> ReadAggregateAsync(
        string tenantId, string solicitationId, CancellationToken cancellationToken = default) =>
        _inner.ReadAggregateAsync(tenantId, solicitationId, cancellationToken);

    public Task<WorkChainMutationReceipt> TriageTaskAsync(
        WorkTaskLifecycleCommand command, CancellationToken cancellationToken = default) =>
        _inner.TriageTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> MarkTaskReadyAsync(
        WorkTaskLifecycleCommand command, CancellationToken cancellationToken = default) =>
        _inner.MarkTaskReadyAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> AssignTaskAsync(
        WorkTaskAssignmentCommand command, CancellationToken cancellationToken = default) =>
        _inner.AssignTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> AddInstructionVersionAsync(
        WorkInstructionVersionCreateCommand command, CancellationToken cancellationToken = default) =>
        _inner.AddInstructionVersionAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> StartAttemptAsync(
        WorkAttemptStartCommand command, CancellationToken cancellationToken = default) =>
        _inner.StartAttemptAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> CompleteAttemptAsync(
        WorkAttemptCompleteCommand command, CancellationToken cancellationToken = default) =>
        _inner.CompleteAttemptAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> CompleteAndApproveAttemptAsync(
        WorkAttemptCompleteCommand command, CancellationToken cancellationToken = default) =>
        _inner.CompleteAndApproveAttemptAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> ExpireAttemptLeaseAsync(
        WorkAttemptLeaseExpiredCommand command, CancellationToken cancellationToken = default) =>
        _inner.ExpireAttemptLeaseAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> BlockRunningTaskAsync(
        WorkTaskBlockCommand command, CancellationToken cancellationToken = default) =>
        _inner.BlockRunningTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> UnblockTaskAsync(
        WorkTaskUnblockCommand command, CancellationToken cancellationToken = default) =>
        _inner.UnblockTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> ReplanEscalatedTaskAsync(
        WorkTaskReplanCommand command, CancellationToken cancellationToken = default) =>
        _inner.ReplanEscalatedTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> ReviewAttemptAsync(
        WorkAttemptReviewCommand command, CancellationToken cancellationToken = default) =>
        _inner.ReviewAttemptAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> EscalateUnreviewableTaskAsync(
        WorkTaskReviewUnavailableCommand command, CancellationToken cancellationToken = default) =>
        _inner.EscalateUnreviewableTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> EscalateUndispatchableTaskAsync(
        WorkTaskUndispatchableCommand command, CancellationToken cancellationToken = default) =>
        _inner.EscalateUndispatchableTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> CompleteMergedTaskAsync(
        WorkTaskDeliveryCompleteCommand command, CancellationToken cancellationToken = default) =>
        _inner.CompleteMergedTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> CancelRunningTaskAsync(
        WorkTaskCancellationCommand command, CancellationToken cancellationToken = default) =>
        _inner.CancelRunningTaskAsync(command, cancellationToken);

    public Task<WorkChainMutationReceipt> SupersedeTaskAsync(
        WorkTaskSupersessionCommand command, CancellationToken cancellationToken = default) =>
        _inner.SupersedeTaskAsync(command, cancellationToken);
}
