using System.Diagnostics;
using System.Globalization;
using Harness.Host.Workers;
using Harness.Persistence.Abstractions.DurableExecution;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Postgres;
using Harness.Persistence.Sqlite;
using Harness.RecoveryTests.Fixtures;
using Harness.SharedKernel.Time;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Harness.RecoveryTests;

public sealed class ProductionDurableExecutionRecoveryTests
{
    [Fact]
    public async Task SqliteSurvivesSigkillAndResumesWithCompleteAudit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var repositoryRoot = FindRepositoryRoot();
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "gng-2-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactRoot, "recovery.db");
        var readySignalPath = Path.Combine(artifactRoot, "checkpoint.ready");
        Directory.CreateDirectory(artifactRoot);

        try
        {
            await using (var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token))
            {
                Assert.Equal(30, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
                await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                    ProductionDurableRecoveryScenario.ProvisionCommand(),
                    timeout.Token);
                var engine = new SqliteDurableExecutionEngine(dispatcher);
                Assert.Equal(
                    DurableCommandStatus.Applied,
                    (await engine.StartAsync(
                        ProductionDurableRecoveryScenario.StartRequest(),
                        ProductionDurableRecoveryScenario.StartedAt,
                        timeout.Token)).Status);
            }

            await RunAndKillWorkerAsync(
                repositoryRoot,
                "sqlite",
                databasePath,
                readySignalPath,
                timeout.Token);

            await using var restartedDispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            var restartedEngine = new SqliteDurableExecutionEngine(restartedDispatcher);
            await ResumeAndAssertAsync(restartedEngine, timeout.Token);
            var evidence = await ReadSqliteEvidenceAsync(restartedDispatcher, timeout.Token);
            AssertEvidence(evidence);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PostgresSurvivesSigkillAndResumesWithCompleteAudit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var repositoryRoot = FindRepositoryRoot();
        await using var fixture = await ManagedRecoveryPostgresFixture.StartAsync(
            repositoryRoot,
            timeout.Token);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        Assert.Equal(11, await PostgresMigrationRunner.ApplyAsync(dataSource, timeout.Token));
        await new PostgresFoundationTransactionStore(dataSource).ProvisionProjectAsync(
            ProductionDurableRecoveryScenario.ProvisionCommand(),
            timeout.Token);
        var engine = new PostgresDurableExecutionEngine(dataSource);
        Assert.Equal(
            DurableCommandStatus.Applied,
            (await engine.StartAsync(
                ProductionDurableRecoveryScenario.StartRequest(),
                ProductionDurableRecoveryScenario.StartedAt,
                timeout.Token)).Status);

        var readySignalPath = Path.Combine(
            Path.GetDirectoryName(fixture.ConnectionReferencePath)!,
            "checkpoint.ready");
        await RunAndKillWorkerAsync(
            repositoryRoot,
            "postgres",
            fixture.ConnectionReferencePath,
            readySignalPath,
            timeout.Token);

        await ResumeAndAssertAsync(engine, timeout.Token);
        var evidence = await ReadPostgresEvidenceAsync(dataSource, timeout.Token);
        AssertEvidence(evidence);
    }

    private static async Task ResumeAndAssertAsync(
        IDurableExecutionEngine engine,
        CancellationToken cancellationToken)
    {
        var interrupted = await engine.GetAsync(
            ProductionDurableRecoveryScenario.TenantId,
            ProductionDurableRecoveryScenario.ExecutionId,
            cancellationToken);
        Assert.NotNull(interrupted);
        Assert.Equal(DurableExecutionState.Running, interrupted.State);
        Assert.Equal(1, interrupted.AttemptCount);
        Assert.Equal(ProductionDurableRecoveryScenario.WorkerOwner, interrupted.ActiveOwner);
        Assert.Equal("step-3", interrupted.LatestCheckpointKey);

        var interruptedLease = new DurableExecutionLease(
            interrupted.TenantId,
            interrupted.ProjectId,
            interrupted.ExecutionId,
            interrupted.ActiveAttemptId!,
            interrupted.AttemptCount,
            interrupted.ActiveOwner!,
            interrupted.FencingToken,
            interrupted.LeaseExpiresAt!.Value,
            interrupted.PayloadJson,
            interrupted.LatestCheckpointKey,
            interrupted.LatestCheckpointJson);
        var recoveryAt = ProductionDurableRecoveryScenario.StartedAt.AddMinutes(1);
        var clock = new MutableClock(recoveryAt);
        var watchdog = CreateWatchdog(engine, clock);
        var reconciliation = await watchdog.RunOnceAsync(cancellationToken);
        Assert.Equal(1, reconciliation.Requeued);
        Assert.Equal(0, reconciliation.DeadLettered);
        Assert.Equal(
            [ProductionDurableRecoveryScenario.ExecutionId],
            reconciliation.ExecutionIds);

        var restartReplay = await CreateWatchdog(engine, clock).RunOnceAsync(cancellationToken);
        Assert.Equal(0, restartReplay.Requeued);
        Assert.Equal(0, restartReplay.DeadLettered);
        Assert.Empty(restartReplay.ExecutionIds);

        var checkpointReplay = await engine.CheckpointAsync(
            ProductionDurableRecoveryScenario.Checkpoint(interruptedLease, 3),
            cancellationToken);
        Assert.Equal(DurableCommandStatus.IdempotentReplay, checkpointReplay.Status);

        var retryAt = recoveryAt.AddSeconds(1);
        clock.UtcNow = retryAt;
        Assert.Equal(1, (await watchdog.RunOnceAsync(cancellationToken)).TimersFired);
        var recoveryLease = await engine.TryAcquireNextAsync(
            ProductionDurableRecoveryScenario.TenantId,
            ProductionDurableRecoveryScenario.RecoveryOwner,
            retryAt,
            TimeSpan.FromSeconds(30),
            cancellationToken);
        Assert.NotNull(recoveryLease);
        Assert.Equal(2, recoveryLease.AttemptNumber);
        Assert.True(recoveryLease.FencingToken > interruptedLease.FencingToken);
        Assert.Equal("step-3", recoveryLease.LatestCheckpointKey);

        for (var step = 4; step <= 6; step++)
        {
            Assert.Equal(
                DurableCommandStatus.Applied,
                (await engine.CheckpointAsync(
                    ProductionDurableRecoveryScenario.Checkpoint(recoveryLease, step),
                    cancellationToken)).Status);
        }

        Assert.Equal(
            DurableExecutionState.Completed,
            (await engine.CompleteAsync(
                new DurableLeaseCommand(
                    recoveryLease.TenantId,
                    recoveryLease.ExecutionId,
                    recoveryLease.AttemptId,
                    recoveryLease.Owner,
                    recoveryLease.FencingToken,
                    retryAt.AddSeconds(7),
                    "recovery:complete"),
                cancellationToken)).State);
        var completed = await engine.GetAsync(
            ProductionDurableRecoveryScenario.TenantId,
            ProductionDurableRecoveryScenario.ExecutionId,
            cancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(DurableExecutionState.Completed, completed.State);
        Assert.Equal(2, completed.AttemptCount);
        Assert.Null(completed.ActiveAttemptId);
        Assert.Equal("step-6", completed.LatestCheckpointKey);
    }

    private static DurableExecutionWatchdogBackgroundService CreateWatchdog(
        IDurableExecutionEngine engine,
        IClock clock) =>
        new(
            engine,
            clock,
            new DurableExecutionWatchdogOptions(
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromSeconds(30)),
            NullLogger<DurableExecutionWatchdogBackgroundService>.Instance);

    private static void AssertEvidence(RecoveryEvidence evidence)
    {
        Assert.Equal(2, evidence.AttemptCount);
        Assert.Equal(["abandoned", "completed"], evidence.AttemptStates);
        Assert.Equal(6, evidence.CheckpointCount);
        Assert.Equal(
            ["step-1", "step-2", "step-3", "step-4", "step-5", "step-6"],
            evidence.CheckpointKeys);
        Assert.Equal(8, evidence.DurableInboxCount);
        Assert.Equal(6, evidence.TransitionCount);
        Assert.Equal([1L, 2, 3, 4, 5, 6], evidence.TransitionSequences);
        Assert.Equal(
            [
                "execution.started",
                "attempt.acquired",
                "attempt.reconciled",
                "retry.timerFired",
                "attempt.acquired",
                "attempt.completed",
            ],
            evidence.TransitionReasons);
        Assert.Equal(6, evidence.OutboxCount);
        Assert.Equal([1L, 2, 3, 4, 5, 6], evidence.OutboxTransitionSequences);
        Assert.Equal(7, evidence.Ledger.Count);

        var previousHash = AuditLedgerHash.Genesis;
        for (var index = 0; index < evidence.Ledger.Count; index++)
        {
            var entry = evidence.Ledger[index];
            Assert.Equal(index + 1, entry.Sequence);
            Assert.Equal(previousHash, entry.PreviousHash);
            Assert.Equal(
                AuditLedgerHash.Compute(
                    previousHash,
                    ProductionDurableRecoveryScenario.TenantId,
                    entry.Sequence,
                    entry.EventType,
                    entry.PayloadJson,
                    entry.OccurredAt),
                entry.EventHash);
            previousHash = entry.EventHash;
        }

        Assert.Equal("project.created", evidence.Ledger[0].EventType);
        Assert.All(
            evidence.Ledger.Skip(1),
            entry => Assert.Equal("durable.stateChanged", entry.EventType));
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private static Task<RecoveryEvidence> ReadSqliteEvidenceAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        dispatcher.ExecuteAsync(
            (connection, token) => ReadSqliteEvidenceCoreAsync(connection, token),
            cancellationToken);

    private static async Task<RecoveryEvidence> ReadSqliteEvidenceCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var attemptStates = await ReadSqliteStringsAsync(
            connection,
            "SELECT state FROM durable_attempts WHERE execution_id = $id ORDER BY attempt_number;",
            cancellationToken);
        var checkpoints = await ReadSqliteStringsAsync(
            connection,
            "SELECT checkpoint_key FROM durable_checkpoints WHERE execution_id = $id ORDER BY created_at;",
            cancellationToken);
        var transitions = await ReadSqlitePairsAsync(
            connection,
            "SELECT sequence, reason FROM durable_transitions WHERE execution_id = $id ORDER BY sequence;",
            cancellationToken);
        var outbox = await ReadSqliteLongsAsync(
            connection,
            "SELECT transition_sequence FROM durable_execution_outbox WHERE execution_id = $id ORDER BY transition_sequence;",
            cancellationToken);
        var ledger = new List<LedgerEntry>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT sequence, previous_hash, event_hash, event_type, payload_json, occurred_at
                FROM audit_ledger WHERE tenant_id = $tenantId ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$tenantId", ProductionDurableRecoveryScenario.TenantId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ledger.Add(new LedgerEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)));
            }
        }

        return new RecoveryEvidence(
            attemptStates.Count,
            attemptStates,
            checkpoints.Count,
            checkpoints,
            await SqliteCountAsync(connection, "durable_command_inbox", cancellationToken),
            transitions.Count,
            transitions.Select(item => item.Number).ToArray(),
            transitions.Select(item => item.Text).ToArray(),
            outbox.Count,
            outbox,
            ledger);
    }

    private static async Task<RecoveryEvidence> ReadPostgresEvidenceAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var attemptStates = await ReadPostgresStringsAsync(
            dataSource,
            "SELECT state FROM harness.durable_attempts WHERE execution_id = $1 ORDER BY attempt_number;",
            cancellationToken);
        var checkpoints = await ReadPostgresStringsAsync(
            dataSource,
            "SELECT checkpoint_key FROM harness.durable_checkpoints WHERE execution_id = $1 ORDER BY created_at;",
            cancellationToken);
        var transitions = await ReadPostgresPairsAsync(
            dataSource,
            "SELECT sequence, reason FROM harness.durable_transitions WHERE execution_id = $1 ORDER BY sequence;",
            cancellationToken);
        var outbox = await ReadPostgresLongsAsync(
            dataSource,
            "SELECT transition_sequence FROM harness.durable_execution_outbox WHERE execution_id = $1 ORDER BY transition_sequence;",
            cancellationToken);
        var ledger = new List<LedgerEntry>();
        await using (var command = dataSource.CreateCommand(
            """
            SELECT sequence, previous_hash, event_hash, event_type, payload_json::text, occurred_at
            FROM harness.audit_ledger WHERE tenant_id = $1 ORDER BY sequence;
            """))
        {
            command.Parameters.Add(new NpgsqlParameter<string>
            {
                TypedValue = ProductionDurableRecoveryScenario.TenantId,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ledger.Add(new LedgerEntry(
                    reader.GetInt64(0),
                    reader.GetString(1).TrimEnd(),
                    reader.GetString(2).TrimEnd(),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        return new RecoveryEvidence(
            attemptStates.Count,
            attemptStates,
            checkpoints.Count,
            checkpoints,
            await PostgresCountAsync(dataSource, "harness.durable_command_inbox", cancellationToken),
            transitions.Count,
            transitions.Select(item => item.Number).ToArray(),
            transitions.Select(item => item.Text).ToArray(),
            outbox.Count,
            outbox,
            ledger);
    }

    private static async Task RunAndKillWorkerAsync(
        string repositoryRoot,
        string provider,
        string targetReference,
        string readySignalPath,
        CancellationToken cancellationToken)
    {
        var dotnet = Path.Combine(repositoryRoot, "tools", "backend", ".tooling", "dotnet", "dotnet");
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(RecoveryFixtureProgram).Assembly.Location);
        startInfo.ArgumentList.Add("production-durable-worker");
        startInfo.ArgumentList.Add(provider);
        startInfo.ArgumentList.Add(targetReference);
        startInfo.ArgumentList.Add(readySignalPath);
        using var worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start production durable worker.");
        var standardOutput = worker.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = worker.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            while (!File.Exists(readySignalPath))
            {
                if (worker.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Production worker exited early. stdout={await standardOutput} stderr={await standardError}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }

            using var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                ArgumentList = { "-9", worker.Id.ToString(CultureInfo.InvariantCulture) },
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Could not start /bin/kill.");
            var error = await kill.StandardError.ReadToEndAsync(cancellationToken);
            await kill.WaitForExitAsync(cancellationToken);
            Assert.True(kill.ExitCode == 0, $"kill -9 failed: {error}");
            await worker.WaitForExitAsync(cancellationToken);
            Assert.NotEqual(0, worker.ExitCode);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSqliteStringsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", ProductionDurableRecoveryScenario.ExecutionId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<(long Number, string Text)>> ReadSqlitePairsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", ProductionDurableRecoveryScenario.ExecutionId);
        var result = new List<(long, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<long>> ReadSqliteLongsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", ProductionDurableRecoveryScenario.ExecutionId);
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static async Task<long> SqliteCountAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE tenant_id = $tenantId;";
        command.Parameters.AddWithValue("$tenantId", ProductionDurableRecoveryScenario.TenantId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ReadPostgresStringsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = ProductionDurableRecoveryScenario.ExecutionId,
        });
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<(long Number, string Text)>> ReadPostgresPairsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = ProductionDurableRecoveryScenario.ExecutionId,
        });
        var result = new List<(long, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<long>> ReadPostgresLongsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = ProductionDurableRecoveryScenario.ExecutionId,
        });
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static async Task<long> PostgresCountAsync(
        NpgsqlDataSource dataSource,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT COUNT(*) FROM {table} WHERE tenant_id = $1;");
        command.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = ProductionDurableRecoveryScenario.TenantId,
        });
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return a count."));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed record RecoveryEvidence(
        int AttemptCount,
        IReadOnlyList<string> AttemptStates,
        int CheckpointCount,
        IReadOnlyList<string> CheckpointKeys,
        long DurableInboxCount,
        int TransitionCount,
        IReadOnlyList<long> TransitionSequences,
        IReadOnlyList<string> TransitionReasons,
        int OutboxCount,
        IReadOnlyList<long> OutboxTransitionSequences,
        IReadOnlyList<LedgerEntry> Ledger);

    private sealed record LedgerEntry(
        long Sequence,
        string PreviousHash,
        string EventHash,
        string EventType,
        string PayloadJson,
        DateTimeOffset OccurredAt);
}
