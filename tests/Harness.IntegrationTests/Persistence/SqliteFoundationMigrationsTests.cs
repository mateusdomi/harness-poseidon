using System.Globalization;
using Harness.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Harness.IntegrationTests.Persistence;

public sealed class SqliteFoundationMigrationsTests
{
    [Fact]
    public async Task RunnerMessageStoreMatchesDualProviderBehavior()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-runner-store-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactRoot, "runner.db");
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using (var store = new SqliteRunnerMessageStore(databasePath))
            {
                await RunnerMessageStoreBehavior.AssertAsync(
                    store,
                    "attempt-dual-sqlite",
                    timeout.Token);
            }

            await using var restartedStore = new SqliteRunnerMessageStore(databasePath);
            var recovered = await restartedStore.ReadAttemptAsync("attempt-dual-sqlite", timeout.Token);
            Assert.NotNull(recovered);
            Assert.Equal(3, recovered.LastSequence);
            Assert.Equal(3, recovered.InboxCount);
            Assert.Equal(3, recovered.OutboxCount);
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
    public async Task FoundationTransactionIsAtomicIdempotentAndAudited()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-foundation-transaction-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(artifactRoot);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(artifactRoot, "foundation.db"),
                timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await FoundationTransactionBehavior.AssertAsync(
                new SqliteFoundationTransactionStore(dispatcher),
                timeout.Token);
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
    public async Task FoundationMigrationIsIdempotentAndEnforcesTenantRelationships()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var artifactRoot = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f1-foundation-sqlite",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactRoot, "foundation.db");
        Directory.CreateDirectory(artifactRoot);

        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            Assert.Equal(4, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
            Assert.Equal(0, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));

            var tableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*)
                        FROM sqlite_master
                        WHERE type = 'table'
                          AND name IN ('tenants', 'organizations', 'projects', 'local_users',
                                       'inbox_messages', 'outbox_messages', 'audit_ledger');
                        """;
                    return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(7, tableCount);

            var durableTableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*)
                        FROM sqlite_master
                        WHERE type = 'table'
                          AND name IN ('durable_executions', 'durable_attempts', 'durable_checkpoints',
                                       'durable_timers', 'durable_signals', 'durable_transitions',
                                       'durable_dead_letters', 'durable_command_inbox',
                                       'durable_execution_outbox');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token),
                        CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(9, durableTableCount);

            var workChainTableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*)
                        FROM sqlite_master
                        WHERE type = 'table'
                          AND name IN ('solicitations', 'demands', 'work_tasks',
                                       'instruction_versions', 'work_attempts',
                                       'work_evidence', 'work_reviews');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token),
                        CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(7, workChainTableCount);

            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO tenants (id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Tenant', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO organizations (id, tenant_id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAW', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Organization', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAV', '01ARZ3NDEKTSV4RRFFQ69G5FAW', 'Project', '2026-07-18T13:30:00.0000000+00:00');
                        INSERT INTO local_users (id, tenant_id, display_name, created_at)
                        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAY', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Local User', '2026-07-18T13:30:00.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                timeout.Token);

            var exception = await Assert.ThrowsAsync<SqliteException>(() =>
                dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAZ', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                                    '01ARZ3NDEKTSV4RRFFQ69G5FB0', 'Invalid', '2026-07-18T13:30:00.0000000+00:00');
                            """;
                        await command.ExecuteNonQueryAsync(token);
                    },
                    timeout.Token));
            Assert.Equal(19, exception.SqliteErrorCode);

            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = WorkChainInsertSql;
                    await command.ExecuteNonQueryAsync(token);
                },
                timeout.Token);
            var duplicateActiveAttempt = await Assert.ThrowsAsync<SqliteException>(() =>
                dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            INSERT INTO work_attempts
                                (id, tenant_id, project_id, task_id, instruction_version_id,
                                 attempt_number, producer_agent_id, state, started_at)
                            VALUES
                                ('01ARZ3NDEKTSV4RRFFQ69G5FE7', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                                 '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2',
                                 '01ARZ3NDEKTSV4RRFFQ69G5FE3', 2, 'engineer-2', 'running',
                                 '2026-07-18T16:00:01.0000000+00:00');
                            """;
                        await command.ExecuteNonQueryAsync(token);
                    },
                    timeout.Token));
            Assert.Equal(19, duplicateActiveAttempt.SqliteErrorCode);

            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO durable_executions
                            (id, tenant_id, project_id, state, payload_json, max_attempts,
                             retry_initial_ms, retry_multiplier, retry_maximum_ms, available_at,
                             created_at, updated_at)
                        VALUES
                            ('01ARZ3NDEKTSV4RRFFQ69G5FB6', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                             '01ARZ3NDEKTSV4RRFFQ69G5FAX', 'ready', '{}', 3,
                             1000, '2.0', 30000, '2026-07-18T14:10:00.0000000+00:00',
                             '2026-07-18T14:10:00.0000000+00:00', '2026-07-18T14:10:00.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                timeout.Token);

            var invalidState = await Assert.ThrowsAsync<SqliteException>(() =>
                dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            INSERT INTO durable_executions
                                (id, tenant_id, project_id, state, payload_json, max_attempts,
                                 retry_initial_ms, retry_multiplier, retry_maximum_ms, available_at,
                                 created_at, updated_at)
                            VALUES
                                ('01ARZ3NDEKTSV4RRFFQ69G5FB7', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                                 '01ARZ3NDEKTSV4RRFFQ69G5FAX', 'unknown', '{}', 3,
                                 1000, '2.0', 30000, '2026-07-18T14:10:00.0000000+00:00',
                                 '2026-07-18T14:10:00.0000000+00:00', '2026-07-18T14:10:00.0000000+00:00');
                            """;
                        await command.ExecuteNonQueryAsync(token);
                    },
                    timeout.Token));
            Assert.Equal(19, invalidState.SqliteErrorCode);
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
        }
    }

    private const string WorkChainInsertSql =
        """
        INSERT INTO solicitations (id, tenant_id, project_id, user_id, content, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE0', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAY',
                'Request', '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO demands
            (id, tenant_id, project_id, solicitation_id, title, acceptance_criteria_json, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE1', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE0',
                'Demand', '["green"]', '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO work_tasks
            (id, tenant_id, project_id, demand_id, title, risk_tier, weight, state, created_at, updated_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE2', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE1',
                'Task', 'medium', 3, 'running', '2026-07-18T16:00:00.0000000+00:00',
                '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO instruction_versions
            (id, tenant_id, project_id, task_id, version, content, content_hash, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE3', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2', 1,
                'Instruction', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO work_attempts
            (id, tenant_id, project_id, task_id, instruction_version_id,
             attempt_number, producer_agent_id, state, started_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE4', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2',
                '01ARZ3NDEKTSV4RRFFQ69G5FE3', 1, 'engineer', 'running',
                '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO work_evidence
            (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE5', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE4', 1,
                'test:green', '2026-07-18T16:00:00.0000000+00:00');
        INSERT INTO work_reviews
            (id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE6', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE4',
                'critic', 'approved', 'Evidence reviewed.', '2026-07-18T16:00:00.0000000+00:00');
        """;
}
