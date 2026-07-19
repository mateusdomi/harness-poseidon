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
            Assert.Equal(31, await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token));
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

            var outboxDispatchSchema = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT
                            (SELECT COUNT(*) FROM sqlite_master
                             WHERE type='table' AND name='outbox_dispatch_failures'),
                            (SELECT COUNT(*) FROM pragma_table_info('outbox_messages')
                             WHERE name IN ('available_at','lock_owner','lock_token','lock_expires_at',
                                            'last_error','dead_lettered_at'));
                        """;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    Assert.True(await reader.ReadAsync(token));
                    return (reader.GetInt32(0), reader.GetInt32(1));
                },
                timeout.Token);
            Assert.Equal((1, 6), outboxDispatchSchema);

            var realtimeTableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*) FROM sqlite_master
                        WHERE type='table' AND name IN ('realtime_streams','realtime_events');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token),
                        CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(2, realtimeTableCount);

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

            var workflowTableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*) FROM sqlite_master
                        WHERE type = 'table' AND name IN
                            ('workflow_definitions', 'workflow_definition_versions',
                             'workflow_phase_definitions', 'workflow_objective_definitions',
                             'workflow_gate_definitions', 'workflow_gate_requirements',
                             'workflow_runs', 'workflow_phase_runs',
                             'workflow_objective_runs', 'workflow_gate_runs');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token),
                        CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(10, workflowTableCount);

            var workflowCatalogProjection = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT
                            (SELECT COUNT(*) FROM sqlite_master WHERE type='table'
                             AND name IN ('workflow_bindings','workflow_risk_acceptances')),
                            (SELECT COUNT(*) FROM pragma_table_info('workflow_runs')
                             WHERE name='workflow_id'),
                            (SELECT COUNT(*) FROM pragma_table_info('workflow_gate_runs')
                             WHERE name IN ('decided_by_profile_id','decision_note')),
                            (SELECT COUNT(*) FROM pragma_table_info('workflow_definition_versions')
                             WHERE name IN ('phase_configs_json','default_operation_mode',
                                            'transitions_json','changelog'));
                        """;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    Assert.True(await reader.ReadAsync(token));
                    return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                },
                timeout.Token);
            Assert.Equal((2, 1, 2, 4), workflowCatalogProjection);

            var organizationColumnCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*) FROM pragma_table_info('organizations') WHERE name IN
                            ('slug','plan','logo_url','primary_color','secondary_color','typography',
                             'default_workflow_template_ids_json','template_keys_json','policies_json');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(9, organizationColumnCount);

            var projectColumnCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name IN
                            ('project_key','description','state','criticality','repository_url',
                             'repository_provider','default_branch','technologies_json','logo_url',
                             'primary_color','secondary_color','typography','member_profile_ids_json',
                             'config_version','chief_agent_id','operation_mode','last_activity_at','deleted_at');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(18, projectColumnCount);

            var conversationTableCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT COUNT(*) FROM sqlite_master
                        WHERE type='table' AND name IN
                            ('conversations','conversation_messages','chat_turns');
                        """;
                    return Convert.ToInt32(
                        await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
                },
                timeout.Token);
            Assert.Equal(3, conversationTableCount);

            var boardProjectionCount = await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT
                            (SELECT COUNT(*) FROM pragma_table_info('solicitations')
                             WHERE name IN ('kind','title','state','supersedes_id','is_internal')),
                            (SELECT COUNT(*) FROM pragma_table_info('demands')
                             WHERE name IN ('description','state','priority','source_solicitation_id','is_internal')),
                            (SELECT COUNT(*) FROM pragma_table_info('work_tasks')
                             WHERE name IN ('source_demand_id','board_state','priority','assignee_agent_id',
                                            'blocked_reason','due_at')),
                            (SELECT COUNT(*) FROM sqlite_master
                             WHERE type='table' AND name='attempt_events');
                        """;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    Assert.True(await reader.ReadAsync(token));
                    return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                },
                timeout.Token);
            Assert.Equal((5, 5, 6, 1), boardProjectionCount);

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

            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO realtime_streams
                            (tenant_id,stream_name,last_sequence,created_at,updated_at)
                        VALUES
                            ('01ARZ3NDEKTSV4RRFFQ69G5FAV','project:01ARZ3NDEKTSV4RRFFQ69G5FAX',1,
                             '2026-07-18T17:30:00.0000000+00:00','2026-07-18T17:30:00.0000000+00:00');
                        INSERT INTO realtime_events
                            (message_id,tenant_id,stream_name,sequence,event_type,payload_json,occurred_at)
                        VALUES
                            ('01ARZ3NDEKTSV4RRFFQ69G5FB0','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                             'project:01ARZ3NDEKTSV4RRFFQ69G5FAX',1,'task.created','{}',
                             '2026-07-18T17:30:00.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                timeout.Token);

            await Assert.ThrowsAsync<SqliteException>(() =>
                dispatcher.ExecuteAsync(
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            "UPDATE realtime_events SET event_type='task.stateChanged';";
                        await command.ExecuteNonQueryAsync(token);
                    },
                    timeout.Token));

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

            await ValidateWorkflowSchemaAsync(dispatcher, timeout.Token);
            await ValidateDocumentSchemaAsync(dispatcher, timeout.Token);

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

    private static async Task ValidateWorkflowSchemaAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = WorkflowInsertSql;
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

        var duplicateActivePhase = await Assert.ThrowsAsync<SqliteException>(() =>
            dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO workflow_phase_runs
                            (id, tenant_id, project_id, workflow_run_id, phase_definition_id,
                             phase_order, state, activated_at)
                        VALUES
                            ('01ARZ3NDEKTSV4RRFFQ69G5FGC', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                             '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG6',
                             '01ARZ3NDEKTSV4RRFFQ69G5FGB', 2, 'active',
                             '2026-07-18T16:30:01.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken));
        Assert.Equal(19, duplicateActivePhase.SqliteErrorCode);
    }

    private static async Task ValidateDocumentSchemaAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var tableCount = await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN
                        ('documents', 'document_versions', 'document_classifications',
                         'document_approval_requests', 'document_state_transitions');
                    """;
                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            cancellationToken);
        Assert.Equal(5, tableCount);

        await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO documents
                        (id,tenant_id,project_id,title,kind,state,current_version,
                         phase_name,inconsistent,version,created_at,updated_at)
                    VALUES
                        ('01ARZ3NDEKTSV4RRFFQ69G5FH0','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAX','Delivery spec','spec',
                         'awaiting_approval',1,NULL,0,3,
                         '2026-07-18T17:00:00.0000000+00:00',
                         '2026-07-18T17:02:00.0000000+00:00');
                    INSERT INTO document_versions
                        (id,tenant_id,project_id,document_id,version,catalog_path,
                         content_hash,supersedes_id,author_kind,author_id,created_at)
                    VALUES
                        ('01ARZ3NDEKTSV4RRFFQ69G5FH1','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',1,
                         'documents/spec-v1.md',
                         'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                         NULL,'agent','01ARZ3NDEKTSV4RRFFQ69G5FAY',
                         '2026-07-18T17:00:00.0000000+00:00');
                    INSERT INTO document_classifications
                        (tenant_id,project_id,document_id,label,ordinal)
                    VALUES
                        ('01ARZ3NDEKTSV4RRFFQ69G5FAV','01ARZ3NDEKTSV4RRFFQ69G5FAX',
                         '01ARZ3NDEKTSV4RRFFQ69G5FH0','requirements',1);
                    INSERT INTO document_approval_requests
                        (id,tenant_id,project_id,document_id,document_version_id,
                         title,description,priority,due_at,state,requested_by_agent_id,requested_at)
                    VALUES
                        ('01ARZ3NDEKTSV4RRFFQ69G5FH2','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',
                         '01ARZ3NDEKTSV4RRFFQ69G5FH1','Approve spec','Review current version',
                         'high','2026-07-20T17:00:00.0000000+00:00','pending',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00.0000000+00:00');
                    INSERT INTO document_state_transitions
                        (id,tenant_id,project_id,document_id,document_version,
                         from_state,to_state,actor_kind,actor_id,occurred_at)
                    VALUES
                        ('01ARZ3NDEKTSV4RRFFQ69G5FH3','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',3,
                         'in_review','awaiting_approval','agent',
                         '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00.0000000+00:00');
                    """;
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

        var duplicatePending = await Assert.ThrowsAsync<SqliteException>(() =>
            dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO document_approval_requests
                            (id,tenant_id,project_id,document_id,document_version_id,
                             title,description,priority,state,requested_by_agent_id,requested_at)
                        VALUES
                            ('01ARZ3NDEKTSV4RRFFQ69G5FH4','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                             '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',
                             '01ARZ3NDEKTSV4RRFFQ69G5FH1','Duplicate','Duplicate','low','pending',
                             '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:03:00.0000000+00:00');
                        """;
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken));
        Assert.Equal(19, duplicatePending.SqliteErrorCode);

        var immutableVersion = await Assert.ThrowsAsync<SqliteException>(() =>
            dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "UPDATE document_versions SET catalog_path='changed.md' WHERE id='01ARZ3NDEKTSV4RRFFQ69G5FH1';";
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken));
        Assert.Equal(19, immutableVersion.SqliteErrorCode);

        var immutableTransition = await Assert.ThrowsAsync<SqliteException>(() =>
            dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        "DELETE FROM document_state_transitions WHERE id='01ARZ3NDEKTSV4RRFFQ69G5FH3';";
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken));
        Assert.Equal(19, immutableTransition.SqliteErrorCode);

        var orphanCount = await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM documents WHERE phase_name IS NULL;";
                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            },
            cancellationToken);
        Assert.Equal(1, orphanCount);
    }

    private const string WorkflowInsertSql =
        """
        INSERT INTO workflow_definitions (id, tenant_id, name, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG0', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                'Delivery', '2026-07-18T16:30:00.0000000+00:00');
        INSERT INTO workflow_definition_versions
            (id, tenant_id, definition_id, version, status, content_hash, created_at, published_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG1', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FG0', 1, 'published',
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                '2026-07-18T16:30:00.0000000+00:00', '2026-07-18T16:30:00.0000000+00:00');
        INSERT INTO workflow_phase_definitions
            (id, tenant_id, definition_version_id, phase_key, name, phase_order)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'analysis', 'Analysis', 1),
            ('01ARZ3NDEKTSV4RRFFQ69G5FGB', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'delivery', 'Delivery', 2);
        INSERT INTO workflow_objective_definitions
            (id, tenant_id, phase_definition_id, objective_key, name, kind, weight)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG3', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG2', 'requirements', 'Requirements', 'document', 2),
            ('01ARZ3NDEKTSV4RRFFQ69G5FG4', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG2', 'analysis-gate', 'Analysis gate', 'gate', 1);
        INSERT INTO workflow_gate_definitions
            (id, tenant_id, phase_definition_id, objective_definition_id,
             gate_key, name, minimum_required_state)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG5', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FG4',
                'analysis-gate', 'Analysis gate', 'validated');
        INSERT INTO workflow_gate_requirements
            (phase_definition_id, gate_definition_id, objective_definition_id, requirement_order)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FG5',
                '01ARZ3NDEKTSV4RRFFQ69G5FG3', 1);
        INSERT INTO workflow_runs
            (id, tenant_id, project_id, definition_version_id, state, created_at, started_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG6', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'running',
                '2026-07-18T16:30:00.0000000+00:00', '2026-07-18T16:30:00.0000000+00:00');
        INSERT INTO workflow_phase_runs
            (id, tenant_id, project_id, workflow_run_id, phase_definition_id,
             phase_order, state, activated_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG7', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG6',
                '01ARZ3NDEKTSV4RRFFQ69G5FG2', 1, 'active',
                '2026-07-18T16:30:00.0000000+00:00');
        INSERT INTO workflow_objective_runs
            (id, tenant_id, project_id, phase_run_id, objective_definition_id, state, updated_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG8', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
             '01ARZ3NDEKTSV4RRFFQ69G5FG3', 'validated', '2026-07-18T16:30:00.0000000+00:00'),
            ('01ARZ3NDEKTSV4RRFFQ69G5FG9', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
             '01ARZ3NDEKTSV4RRFFQ69G5FG4', 'pending', '2026-07-18T16:30:00.0000000+00:00');
        INSERT INTO workflow_gate_runs
            (id, tenant_id, project_id, phase_run_id, gate_definition_id, state)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FGA', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
                '01ARZ3NDEKTSV4RRFFQ69G5FG5', 'pending');
        """;

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
