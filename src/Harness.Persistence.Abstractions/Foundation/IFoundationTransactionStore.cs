namespace Harness.Persistence.Abstractions.Foundation;

public interface IFoundationTransactionStore
{
    Task<ProjectProvisionReceipt> ProvisionProjectAsync(
        ProjectProvisionCommand command,
        CancellationToken cancellationToken = default);

    Task<FoundationStoreSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken = default);
}
