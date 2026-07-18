using System.Text.Json;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Postgres;
using Harness.Persistence.Sqlite;
using Npgsql;

namespace Harness.RecoveryTests.Fixtures;

internal static class ProductionDurableRecoveryScenario
{
    public const string TenantId = "01ARZ3NDEKTSV4RRFFQ69G5FC0";
    public const string OrganizationId = "01ARZ3NDEKTSV4RRFFQ69G5FC1";
    public const string ProjectId = "01ARZ3NDEKTSV4RRFFQ69G5FC2";
    public const string UserId = "01ARZ3NDEKTSV4RRFFQ69G5FC3";
    public const string ExecutionId = "01ARZ3NDEKTSV4RRFFQ69G5FC4";
    public const string WorkerOwner = "owner-before-sigkill";
    public const string RecoveryOwner = "owner-after-restart";

    public static DateTimeOffset StartedAt { get; } =
        new(2026, 7, 18, 15, 10, 0, TimeSpan.Zero);

    public static ProjectProvisionCommand ProvisionCommand() => new(
        TenantId,
        "Recovery Tenant",
        OrganizationId,
        "Recovery Organization",
        ProjectId,
        "Recovery Project",
        UserId,
        "Recovery User",
        "recovery:foundation",
        new string('A', 64),
        "01ARZ3NDEKTSV4RRFFQ69G5FC5",
        "01ARZ3NDEKTSV4RRFFQ69G5FC6",
        "project.created",
        JsonSerializer.Serialize(new { projectId = ProjectId }),
        StartedAt.AddMinutes(-1));

    public static DurableExecutionStartRequest StartRequest() => new(
        TenantId,
        ProjectId,
        ExecutionId,
        JsonSerializer.Serialize(new { totalSteps = 6 }),
        new DurableRetryPolicy(
            maxAttempts: 3,
            initialDelay: TimeSpan.FromMilliseconds(100),
            multiplier: 2m,
            maximumDelay: TimeSpan.FromSeconds(1)),
        StartedAt,
        "recovery:start");

    public static DurableCheckpointCommand Checkpoint(
        DurableExecutionLease lease,
        int step) => new(
        TenantId,
        ExecutionId,
        lease.AttemptId,
        lease.Owner,
        lease.FencingToken,
        $"step-{step}",
        JsonSerializer.Serialize(new { completedSteps = step }),
        StartedAt.AddSeconds(step),
        $"recovery:checkpoint:{step}");

    public static async Task RunSqliteWorkerAsync(
        string databasePath,
        string readySignalPath,
        CancellationToken cancellationToken)
    {
        await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, cancellationToken);
        await RunWorkerAsync(
            new SqliteDurableExecutionEngine(dispatcher),
            readySignalPath,
            cancellationToken);
    }

    public static async Task RunPostgresWorkerAsync(
        string connectionReferencePath,
        string readySignalPath,
        CancellationToken cancellationToken)
    {
        var connectionString = await File.ReadAllTextAsync(connectionReferencePath, cancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await RunWorkerAsync(
            new PostgresDurableExecutionEngine(dataSource),
            readySignalPath,
            cancellationToken);
    }

    private static async Task RunWorkerAsync(
        IDurableExecutionEngine engine,
        string readySignalPath,
        CancellationToken cancellationToken)
    {
        var lease = await engine.TryAcquireNextAsync(
            TenantId,
            WorkerOwner,
            StartedAt,
            TimeSpan.FromSeconds(30),
            cancellationToken)
            ?? throw new InvalidOperationException("The production durable execution was not acquired.");

        for (var step = 1; step <= 3; step++)
        {
            var result = await engine.CheckpointAsync(Checkpoint(lease, step), cancellationToken);
            if (result.Status != DurableCommandStatus.Applied)
            {
                throw new InvalidOperationException(
                    $"Checkpoint {step} was not applied: {result.Status}.");
            }
        }

        await File.WriteAllTextAsync(readySignalPath, lease.AttemptId, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
