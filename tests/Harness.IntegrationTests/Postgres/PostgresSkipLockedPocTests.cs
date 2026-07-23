using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Harness.Modules.Execution.Infrastructure.Sandbox;
using Harness.IntegrationTests.Persistence;
using Harness.IntegrationTests.Workers;
using Harness.Persistence.Postgres;
using Npgsql;

namespace Harness.IntegrationTests.Postgres;

[Collection("managed-postgres")]
public sealed class PostgresSkipLockedPocTests
{
    [Fact]
    public async Task ManagedPostgresUsesIndependentMigrationsSkipLockedAndFencing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var fixture = await ManagedPostgresFixture.StartAsync(timeout.Token);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        var store = new PostgresWorkItemStore(dataSource);

        Assert.Equal(61, await store.ApplyMigrationsAsync(timeout.Token));
        Assert.Equal(0, await store.ApplyMigrationsAsync(timeout.Token));
        await ValidateFoundationSchemaAsync(dataSource, timeout.Token);
        await FoundationTransactionBehavior.AssertAsync(
            new PostgresFoundationTransactionStore(dataSource),
            timeout.Token);
        await AssertAuditLedgerIsAppendOnlyAsync(dataSource, timeout.Token);
        await new PostgresFoundationTransactionStore(dataSource).ProvisionProjectAsync(
            OutboxStoreBehavior.SecondProjectCommand(),
            timeout.Token);
        await OutboxStoreBehavior.AssertAsync(
            new PostgresOutboxStore(dataSource),
            timeout.Token);
        await RealtimeEventStoreBehavior.AssertAsync(
            new PostgresRealtimeEventStore(dataSource),
            timeout.Token);
        await ValidateDurableSchemaAsync(dataSource, timeout.Token);
        await ValidateWorkChainSchemaAsync(dataSource, timeout.Token);
        await WorkChainStoreBehavior.AssertAsync(
            new PostgresWorkChainStore(dataSource),
            timeout.Token);
        await ValidateWorkflowSchemaAsync(dataSource, timeout.Token);
        await WorkflowStoreBehavior.AssertAsync(
            new PostgresWorkflowStore(dataSource),
            timeout.Token);
        await ValidateDocumentSchemaAsync(dataSource, timeout.Token);
        await DocumentStoreBehavior.AssertAsync(
            new PostgresDocumentStore(dataSource),
            new PostgresDocumentCatalogStore(dataSource),
            timeout.Token);
        var durableEngine = new PostgresDurableExecutionEngine(dataSource);
        await DurableExecutionWatchdogBehavior.AssertAsync(durableEngine, timeout.Token);
        await DurableExecutionEngineBehavior.AssertAsync(durableEngine, timeout.Token);
        await RunnerMessageStoreBehavior.AssertAsync(
            new PostgresRunnerMessageStore(dataSource),
            "attempt-dual-postgres",
            timeout.Token);
        await IdentityCoreStoreBehavior.AssertAsync(
            new PostgresLocalProfileStore(dataSource),
            new PostgresOrganizationStore(dataSource),
            new PostgresProjectStore(dataSource),
            new PostgresAuditEventStore(dataSource),
            timeout.Token);
        var catalogProfile = (await new PostgresLocalProfileStore(dataSource)
            .ListAsync(timeout.Token))[0];
        await CatalogStoreBehavior.AssertAsync(
            new PostgresAgentCatalogStore(dataSource),
            new PostgresToolCatalogStore(dataSource),
            new PostgresProviderCatalogStore(dataSource),
            new PostgresTeamSpecialtyCatalogStore(dataSource),
            catalogProfile.TenantId,
            timeout.Token);
        await GovernanceRuntimeStoreBehavior.AssertAsync(
            new PostgresGovernanceRuntimeStore(dataSource),
            catalogProfile.TenantId,
            timeout.Token);
        var learningScope = await RunConversationChiefParityAsync(dataSource, catalogProfile, timeout.Token);
        await LearningCandidateStoreBehavior.AssertAsync(
            new PostgresLearningCandidateStore(dataSource), catalogProfile.TenantId,
            learningScope.OrganizationId, learningScope.ProjectId, catalogProfile.Id, timeout.Token);

        var createdAt = DateTimeOffset.Parse(
            "2026-07-18T13:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);
        var identifiers = Enumerable.Range(0, 80)
            .Select(index => $"work-{index:D3}")
            .ToArray();
        await Task.WhenAll(identifiers.Select(identifier =>
            store.EnqueueAsync(identifier, "{\"kind\":\"poc-8\"}", createdAt, timeout.Token)));
        var duplicateResults = await Task.WhenAll(identifiers.Select(identifier =>
            store.EnqueueAsync(identifier, "{\"kind\":\"duplicate\"}", createdAt, timeout.Token)));

        Assert.DoesNotContain(true, duplicateResults);
        Assert.Equal(80, await store.CountAsync("ready", timeout.Token));

        var acquired = new ConcurrentBag<PostgresWorkItemLease>();
        var workers = Enumerable.Range(0, 12).Select(async workerIndex =>
        {
            while (true)
            {
                var lease = await store.TryAcquireNextAsync(
                    $"worker-{workerIndex:D2}",
                    TimeSpan.FromMinutes(1),
                    createdAt,
                    timeout.Token);
                if (lease is null)
                {
                    return;
                }

                acquired.Add(lease);
            }
        });
        await Task.WhenAll(workers);

        Assert.Equal(80, acquired.Count);
        Assert.Equal(80, acquired.Select(lease => lease.WorkItemId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(acquired, lease => Assert.Equal(1, lease.FencingToken));
        Assert.Equal(0, await store.CountAsync("ready", timeout.Token));

        await ResetQueueAsync(dataSource, timeout.Token);
        await store.EnqueueAsync("a-locked", "{}", createdAt, timeout.Token);
        await store.EnqueueAsync("b-available", "{}", createdAt, timeout.Token);

        await using (var lockingConnection = await dataSource.OpenConnectionAsync(timeout.Token))
        await using (var lockingTransaction = await lockingConnection.BeginTransactionAsync(timeout.Token))
        {
            await using var lockCommand = lockingConnection.CreateCommand();
            lockCommand.Transaction = lockingTransaction;
            lockCommand.CommandText =
                """
                SELECT id
                FROM harness_poc.work_items
                WHERE state = 'ready'
                ORDER BY created_at, id
                FOR UPDATE
                LIMIT 1;
                """;
            Assert.Equal("a-locked", await lockCommand.ExecuteScalarAsync(timeout.Token));

            var stopwatch = Stopwatch.StartNew();
            var skippedLease = await store.TryAcquireNextAsync(
                "skip-locked-owner",
                TimeSpan.FromMinutes(1),
                createdAt,
                timeout.Token);
            stopwatch.Stop();

            Assert.NotNull(skippedLease);
            Assert.Equal("b-available", skippedLease.WorkItemId);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            await lockingTransaction.RollbackAsync(timeout.Token);
        }

        await ResetQueueAsync(dataSource, timeout.Token);
        await store.EnqueueAsync("fenced-item", "{}", createdAt, timeout.Token);
        var oldLease = await store.TryAcquireNextAsync(
            "owner-old",
            TimeSpan.FromSeconds(1),
            createdAt,
            timeout.Token);
        var newLease = await store.TryAcquireNextAsync(
            "owner-new",
            TimeSpan.FromMinutes(1),
            createdAt.AddSeconds(2),
            timeout.Token);

        Assert.NotNull(oldLease);
        Assert.NotNull(newLease);
        Assert.Equal(1, oldLease.FencingToken);
        Assert.Equal(2, newLease.FencingToken);
        Assert.False(await store.CompleteAsync("fenced-item", oldLease.FencingToken, timeout.Token));
        Assert.True(await store.CompleteAsync("fenced-item", newLease.FencingToken, timeout.Token));

        var inventory = await fixture.Provider.DetectResourcesAsync(fixture.AttemptId, timeout.Token);
        Assert.Single(inventory.Containers);
        Assert.Single(inventory.Networks);
        Assert.Single(inventory.Volumes);
        Assert.Single(inventory.Images);
    }

    private static async Task ResetQueueAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE harness_poc.work_items;");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertAuditLedgerIsAppendOnlyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE harness.audit_ledger SET event_type='tampered' WHERE sequence=1;");
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal("P0001", exception.SqlState);
    }

    private static async Task<(string OrganizationId, string ProjectId)> RunConversationChiefParityAsync(
        NpgsqlDataSource dataSource,
        Harness.Persistence.Abstractions.Identity.LocalProfileRecord profile,
        CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var organizations = new PostgresOrganizationStore(dataSource);
        var projects = new PostgresProjectStore(dataSource);
        var organizationId = Harness.SharedKernel.Identifiers.UlidValue.New(now).ToString();
        var creation = await organizations.CreateAsync(
            new Harness.Persistence.Abstractions.Organizations.OrganizationCreateCommand(
                profile.TenantId, organizationId, $"Chat {organizationId[^6..]}",
                $"chat-{organizationId[^6..].ToLowerInvariant()}", "personal",
                new Harness.Persistence.Abstractions.Organizations.OrganizationBrandRecord(null, null, null, null),
                now),
            token);
        Assert.Equal(
            Harness.Persistence.Abstractions.Organizations.OrganizationMutationStatus.Applied,
            creation.Status);
        var projectId = Harness.SharedKernel.Identifiers.UlidValue.New(now.AddMilliseconds(1)).ToString();
        var chiefAgentId = Harness.SharedKernel.Identifiers.UlidValue.New(now.AddMilliseconds(2)).ToString();
        var project = await projects.CreateAsync(
            new Harness.Persistence.Abstractions.Projects.ProjectCreateCommand(
                profile.TenantId,
                new Harness.Persistence.Abstractions.Projects.ProjectRecord(
                    profile.TenantId, projectId, organizationId, "Chat", "CHAT", "Paridade",
                    "active", "medium", null, "local", "main",
                    [], new Harness.Persistence.Abstractions.Projects.ProjectBrandRecord(null, null, null, null),
                    [profile.Id], 1, chiefAgentId, "manual", now, now, 0),
                now.AddMilliseconds(3)),
            token);
        Assert.Equal(
            Harness.Persistence.Abstractions.Projects.ProjectMutationStatus.Applied,
            project.Status);
        var conversations = new PostgresConversationStore(dataSource);
        await ConversationChiefStoreBehavior.AssertAsync(
            conversations,
            conversations,
            profile.TenantId,
            projectId,
            profile.Id,
            chiefAgentId,
            async tenant =>
            {
                await using var count = dataSource.CreateCommand(
                    "SELECT COUNT(*) FROM harness.demands WHERE tenant_id=$1;");
                count.Parameters.AddWithValue(tenant);
                return Convert.ToInt32(await count.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
            },
            token);
        await BoardWorkflowProjectionBehavior.AssertAsync(
            new PostgresWorkBoardStore(dataSource),
            new PostgresWorkflowStore(dataSource),
            new PostgresWorkflowCatalogStore(dataSource),
            profile.TenantId,
            projectId,
            profile.Id,
            token);
        return (organizationId, projectId);
    }

    private static async Task ValidateFoundationSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using (var countCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'harness'
              AND table_name IN ('tenants', 'organizations', 'projects', 'local_users',
                                 'inbox_messages', 'outbox_messages', 'audit_ledger');
            """))
        {
            Assert.Equal(7L, await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var dispatchSchemaCommand = dataSource.CreateCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM information_schema.tables
                 WHERE table_schema='harness' AND table_name='outbox_dispatch_failures'),
                (SELECT COUNT(*) FROM information_schema.columns
                 WHERE table_schema='harness' AND table_name='outbox_messages'
                   AND column_name IN ('available_at','lock_owner','lock_token','lock_expires_at',
                                       'last_error','dead_lettered_at'));
            """))
        await using (var reader = await dispatchSchemaCommand.ExecuteReaderAsync(cancellationToken))
        {
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(6L, reader.GetInt64(1));
        }

        await using (var realtimeSchemaCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema='harness'
              AND table_name IN ('realtime_streams','realtime_events');
            """))
        {
            Assert.Equal(2L, await realtimeSchemaCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var insertCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.tenants (id, name, created_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Tenant', '2026-07-18T13:30:00Z');
            INSERT INTO harness.organizations (id, tenant_id, name, created_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAW', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Organization', '2026-07-18T13:30:00Z');
            INSERT INTO harness.projects (id, tenant_id, organization_id, name, created_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAV', '01ARZ3NDEKTSV4RRFFQ69G5FAW', 'Project', '2026-07-18T13:30:00Z');
            INSERT INTO harness.local_users (id, tenant_id, display_name, created_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAY', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Local User', '2026-07-18T13:30:00Z');
            """))
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var realtimeInsertCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.realtime_streams
                (tenant_id,stream_name,last_sequence,created_at,updated_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FAV','project:01ARZ3NDEKTSV4RRFFQ69G5FAX',1,
                 '2026-07-18T17:30:00Z','2026-07-18T17:30:00Z');
            INSERT INTO harness.realtime_events
                (message_id,tenant_id,stream_name,sequence,event_type,payload_json,occurred_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FB0','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 'project:01ARZ3NDEKTSV4RRFFQ69G5FAX',1,'task.created','{}',
                 '2026-07-18T17:30:00Z');
            """))
        {
            await realtimeInsertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var realtimeUpdateCommand = dataSource.CreateCommand(
            "UPDATE harness.realtime_events SET event_type='task.stateChanged';"))
        {
            var realtimeException = await Assert.ThrowsAsync<PostgresException>(
                () => realtimeUpdateCommand.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, realtimeException.SqlState);
        }

        await using var invalidCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.projects (id, tenant_id, organization_id, name, created_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAZ', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                    '01ARZ3NDEKTSV4RRFFQ69G5FB0', 'Invalid', '2026-07-18T13:30:00Z');
            """);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalidCommand.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);

        await using var cleanupCommand = dataSource.CreateCommand("TRUNCATE TABLE harness.tenants CASCADE;");
        await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateDurableSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using (var countCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'harness'
              AND table_name IN ('durable_executions', 'durable_attempts', 'durable_checkpoints',
                                 'durable_timers', 'durable_signals', 'durable_transitions',
                                 'durable_dead_letters', 'durable_command_inbox',
                                 'durable_execution_outbox');
            """))
        {
            Assert.Equal(9L, await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var insertCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.durable_executions
                (id, tenant_id, project_id, state, payload_json, max_attempts,
                 retry_initial_ms, retry_multiplier, retry_maximum_ms, available_at,
                 created_at, updated_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FB9', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX', 'ready', '{}', 3,
                 1000, 2.0, 30000, '2026-07-18T14:10:00Z',
                 '2026-07-18T14:10:00Z', '2026-07-18T14:10:00Z');
            """))
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var invalidCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.durable_executions
                (id, tenant_id, project_id, state, payload_json, max_attempts,
                 retry_initial_ms, retry_multiplier, retry_maximum_ms, available_at,
                 created_at, updated_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FBA', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX', 'unknown', '{}', 3,
                 1000, 2.0, 30000, '2026-07-18T14:10:00Z',
                 '2026-07-18T14:10:00Z', '2026-07-18T14:10:00Z');
            """);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => invalidCommand.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);

        await using var cleanupCommand = dataSource.CreateCommand(
            "DELETE FROM harness.durable_executions WHERE id = '01ARZ3NDEKTSV4RRFFQ69G5FB9';");
        await cleanupCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateWorkChainSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var countCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'harness'
              AND table_name IN ('solicitations', 'demands', 'work_tasks',
                                 'instruction_versions', 'work_attempts',
                                 'work_evidence', 'work_reviews');
            """);
        Assert.Equal(7L, await countCommand.ExecuteScalarAsync(cancellationToken));

        await using (var insertCommand = dataSource.CreateCommand(WorkChainInsertSql))
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var duplicateAttempt = dataSource.CreateCommand(
            """
            INSERT INTO harness.work_attempts
                (id, tenant_id, project_id, task_id, instruction_version_id,
                 attempt_number, producer_agent_id, state, started_at)
            VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE7', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                    '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2',
                    '01ARZ3NDEKTSV4RRFFQ69G5FE3', 2, 'engineer-2', 'running',
                    '2026-07-18T16:00:01Z');
            """);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => duplicateAttempt.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    private static async Task ValidateWorkflowSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using (var countCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'harness' AND table_name IN
                ('workflow_definitions', 'workflow_definition_versions',
                 'workflow_phase_definitions', 'workflow_objective_definitions',
                 'workflow_gate_definitions', 'workflow_gate_requirements',
                 'workflow_runs', 'workflow_phase_runs',
                 'workflow_objective_runs', 'workflow_gate_runs');
            """))
        {
            Assert.Equal(10L, await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var insertCommand = dataSource.CreateCommand(WorkflowInsertSql))
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var duplicateActivePhase = dataSource.CreateCommand(
            """
            INSERT INTO harness.workflow_phase_runs
                (id, tenant_id, project_id, workflow_run_id, phase_definition_id,
                 phase_order, state, activated_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FGC', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG6',
                 '01ARZ3NDEKTSV4RRFFQ69G5FGB', 2, 'active', '2026-07-18T16:30:01Z');
            """);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => duplicateActivePhase.ExecuteNonQueryAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    private static async Task ValidateDocumentSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using (var countCommand = dataSource.CreateCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema='harness' AND table_name IN
                ('documents', 'document_versions', 'document_classifications',
                 'document_approval_requests', 'document_state_transitions');
            """))
        {
            Assert.Equal(5L, await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var insertCommand = dataSource.CreateCommand(
            """
            INSERT INTO harness.documents
                (id,tenant_id,project_id,title,kind,state,current_version,
                 phase_name,inconsistent,version,created_at,updated_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FH0','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX','Delivery spec','spec',
                 'awaiting_approval',1,NULL,false,3,
                 '2026-07-18T17:00:00Z','2026-07-18T17:02:00Z');
            INSERT INTO harness.document_versions
                (id,tenant_id,project_id,document_id,version,catalog_path,
                 content_hash,supersedes_id,author_kind,author_id,created_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FH1','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',1,
                 'documents/spec-v1.md',
                 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                 NULL,'agent','01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:00:00Z');
            INSERT INTO harness.document_classifications
                (tenant_id,project_id,document_id,label,ordinal)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FAV','01ARZ3NDEKTSV4RRFFQ69G5FAX',
                 '01ARZ3NDEKTSV4RRFFQ69G5FH0','requirements',1);
            INSERT INTO harness.document_approval_requests
                (id,tenant_id,project_id,document_id,document_version_id,
                 title,description,priority,due_at,state,requested_by_agent_id,requested_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FH2','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',
                 '01ARZ3NDEKTSV4RRFFQ69G5FH1','Approve spec','Review current version',
                 'high','2026-07-20T17:00:00Z','pending',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00Z');
            INSERT INTO harness.document_state_transitions
                (id,tenant_id,project_id,document_id,document_version,
                 from_state,to_state,actor_kind,actor_id,occurred_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FH3','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',3,
                 'in_review','awaiting_approval','agent',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00Z');
            """))
        {
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var duplicatePending = dataSource.CreateCommand(
            """
            INSERT INTO harness.document_approval_requests
                (id,tenant_id,project_id,document_id,document_version_id,
                 title,description,priority,state,requested_by_agent_id,requested_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FH4','01ARZ3NDEKTSV4RRFFQ69G5FAV',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',
                 '01ARZ3NDEKTSV4RRFFQ69G5FH1','Duplicate','Duplicate','low','pending',
                 '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:03:00Z');
            """))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => duplicatePending.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        }

        await using (var immutableVersion = dataSource.CreateCommand(
            "UPDATE harness.document_versions SET catalog_path='changed.md' WHERE id='01ARZ3NDEKTSV4RRFFQ69G5FH1';"))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => immutableVersion.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal("23000", exception.SqlState);
        }

        await using (var immutableTransition = dataSource.CreateCommand(
            "DELETE FROM harness.document_state_transitions WHERE id='01ARZ3NDEKTSV4RRFFQ69G5FH3';"))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => immutableTransition.ExecuteNonQueryAsync(cancellationToken));
            Assert.Equal("23000", exception.SqlState);
        }

        await using var orphanCount = dataSource.CreateCommand(
            "SELECT COUNT(*) FROM harness.documents WHERE phase_name IS NULL;");
        Assert.Equal(1L, await orphanCount.ExecuteScalarAsync(cancellationToken));
    }

    private const string WorkflowInsertSql =
        """
        INSERT INTO harness.workflow_definitions (id, tenant_id, name, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG0', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                'Delivery', '2026-07-18T16:30:00Z');
        INSERT INTO harness.workflow_definition_versions
            (id, tenant_id, definition_id, version, status, content_hash, created_at, published_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG1', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FG0', 1, 'published',
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                '2026-07-18T16:30:00Z', '2026-07-18T16:30:00Z');
        INSERT INTO harness.workflow_phase_definitions
            (id, tenant_id, definition_version_id, phase_key, name, phase_order)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'analysis', 'Analysis', 1),
            ('01ARZ3NDEKTSV4RRFFQ69G5FGB', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'delivery', 'Delivery', 2);
        INSERT INTO harness.workflow_objective_definitions
            (id, tenant_id, phase_definition_id, objective_key, name, kind, weight)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG3', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG2', 'requirements', 'Requirements', 'document', 2),
            ('01ARZ3NDEKTSV4RRFFQ69G5FG4', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FG2', 'analysis-gate', 'Analysis gate', 'gate', 1);
        INSERT INTO harness.workflow_gate_definitions
            (id, tenant_id, phase_definition_id, objective_definition_id,
             gate_key, name, minimum_required_state)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG5', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FG4',
                'analysis-gate', 'Analysis gate', 'validated');
        INSERT INTO harness.workflow_gate_requirements
            (phase_definition_id, gate_definition_id, objective_definition_id, requirement_order)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG2', '01ARZ3NDEKTSV4RRFFQ69G5FG5',
                '01ARZ3NDEKTSV4RRFFQ69G5FG3', 1);
        INSERT INTO harness.workflow_runs
            (id, tenant_id, project_id, definition_version_id, state, created_at, started_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG6', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG1', 'running',
                '2026-07-18T16:30:00Z', '2026-07-18T16:30:00Z');
        INSERT INTO harness.workflow_phase_runs
            (id, tenant_id, project_id, workflow_run_id, phase_definition_id,
             phase_order, state, activated_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FG7', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG6',
                '01ARZ3NDEKTSV4RRFFQ69G5FG2', 1, 'active', '2026-07-18T16:30:00Z');
        INSERT INTO harness.workflow_objective_runs
            (id, tenant_id, project_id, phase_run_id, objective_definition_id, state, updated_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FG8', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
             '01ARZ3NDEKTSV4RRFFQ69G5FG3', 'validated', '2026-07-18T16:30:00Z'),
            ('01ARZ3NDEKTSV4RRFFQ69G5FG9', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
             '01ARZ3NDEKTSV4RRFFQ69G5FG4', 'pending', '2026-07-18T16:30:00Z');
        INSERT INTO harness.workflow_gate_runs
            (id, tenant_id, project_id, phase_run_id, gate_definition_id, state)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FGA', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FG7',
                '01ARZ3NDEKTSV4RRFFQ69G5FG5', 'pending');
        """;

    private const string WorkChainInsertSql =
        """
        INSERT INTO harness.solicitations (id, tenant_id, project_id, user_id, content, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE0', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAY', 'Request',
                '2026-07-18T16:00:00Z');
        INSERT INTO harness.demands
            (id, tenant_id, project_id, solicitation_id, title, acceptance_criteria_json, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE1', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE0', 'Demand',
                '["green"]', '2026-07-18T16:00:00Z');
        INSERT INTO harness.work_tasks
            (id, tenant_id, project_id, demand_id, title, risk_tier, weight, state, created_at, updated_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE2', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE1', 'Task',
                'medium', 3, 'running', '2026-07-18T16:00:00Z', '2026-07-18T16:00:00Z');
        INSERT INTO harness.instruction_versions
            (id, tenant_id, project_id, task_id, version, content, content_hash, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE3', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2', 1, 'Instruction',
                'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                '2026-07-18T16:00:00Z');
        INSERT INTO harness.work_attempts
            (id, tenant_id, project_id, task_id, instruction_version_id,
             attempt_number, producer_agent_id, state, started_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE4', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE2',
                '01ARZ3NDEKTSV4RRFFQ69G5FE3', 1, 'engineer', 'running', '2026-07-18T16:00:00Z');
        INSERT INTO harness.work_evidence
            (id, tenant_id, project_id, attempt_id, ordinal, reference, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE5', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE4', 1, 'test:green',
                '2026-07-18T16:00:00Z');
        INSERT INTO harness.work_reviews
            (id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FE6', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FE4', 'critic',
                'approved', 'Evidence reviewed.', '2026-07-18T16:00:00Z');
        """;

    internal sealed class ManagedPostgresFixture : IAsyncDisposable
    {
        private readonly string _artifactRoot;
        private int _disposed;

        private ManagedPostgresFixture(
            string attemptId,
            string artifactRoot,
            string connectionString,
            DockerSandboxProvider provider)
        {
            AttemptId = attemptId;
            _artifactRoot = artifactRoot;
            ConnectionString = connectionString;
            Provider = provider;
        }

        public string AttemptId { get; }

        public string ConnectionString { get; }

        public DockerSandboxProvider Provider { get; }

        public static async Task<ManagedPostgresFixture> StartAsync(CancellationToken cancellationToken)
        {
            var repositoryRoot = FindRepositoryRoot();
            var attemptId = $"poc8-{Guid.NewGuid():N}"[..17];
            var artifactRoot = Path.Combine(
                AppContext.BaseDirectory,
                "poc-artifacts",
                "poc-8",
                attemptId);
            var secretPath = Path.Combine(artifactRoot, "postgres-password");
            var imageName = $"harness-postgres-poc8:{attemptId}";
            var networkName = $"harness-postgres-network-{attemptId}";
            var volumeName = $"harness-postgres-data-{attemptId}";
            var containerName = $"harness-postgres-poc8-{attemptId}";
            var provider = new DockerSandboxProvider();

            Directory.CreateDirectory(artifactRoot);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await File.WriteAllTextAsync(secretPath, password, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            try
            {
                await provider.BuildImageAsync(
                    attemptId,
                    imageName,
                    Path.Combine(repositoryRoot, "infra", "postgres", "poc8"),
                    cancellationToken);
                await EnsureDockerSuccessAsync(
                    [
                        "network", "create",
                        "--label", DockerSandboxProvider.ManagedLabel,
                        "--label", $"com.harness.attempt={attemptId}",
                        networkName,
                    ],
                    cancellationToken);
                await EnsureDockerSuccessAsync(
                    [
                        "volume", "create",
                        "--label", DockerSandboxProvider.ManagedLabel,
                        "--label", $"com.harness.attempt={attemptId}",
                        volumeName,
                    ],
                    cancellationToken);
                await EnsureDockerSuccessAsync(
                    [
                        "run", "--detach",
                        "--name", containerName,
                        "--label", DockerSandboxProvider.ManagedLabel,
                        "--label", $"com.harness.attempt={attemptId}",
                        "--network", networkName,
                        "--publish=127.0.0.1::5432/tcp",
                        "--mount", $"type=volume,source={volumeName},target=/var/lib/postgresql/data",
                        "--mount", $"type=bind,source={secretPath},target=/run/secrets/postgres-password,readonly",
                        "--env", "POSTGRES_USER=harness",
                        "--env", "POSTGRES_DB=harness_poc",
                        "--env", "POSTGRES_PASSWORD_FILE=/run/secrets/postgres-password",
                        "--memory", "256m",
                        "--cpus", "0.5",
                        "--pids-limit", "128",
                        "--security-opt", "no-new-privileges",
                        "--health-cmd", "pg_isready --username harness --dbname harness_poc",
                        "--health-interval", "1s",
                        "--health-timeout", "2s",
                        "--health-retries", "60",
                        imageName,
                    ],
                    cancellationToken);

                await WaitUntilHealthyAsync(containerName, cancellationToken);
                var publishedPort = await GetPublishedPortAsync(containerName, cancellationToken);
                var connectionString = new NpgsqlConnectionStringBuilder
                {
                    Host = "127.0.0.1",
                    Port = publishedPort,
                    Database = "harness_poc",
                    Username = "harness",
                    Password = password,
                    SslMode = SslMode.Disable,
                    IncludeErrorDetail = false,
                    Timeout = 5,
                    CommandTimeout = 5,
                }.ConnectionString;
                return new ManagedPostgresFixture(attemptId, artifactRoot, connectionString, provider);
            }
            catch
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await provider.CleanupAsync(attemptId, cleanupTimeout.Token);
                Directory.Delete(artifactRoot, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Provider.CleanupAsync(AttemptId, cleanupTimeout.Token);
            Assert.True((await Provider.DetectResourcesAsync(AttemptId, cleanupTimeout.Token)).IsEmpty);
            if (Directory.Exists(_artifactRoot))
            {
                Directory.Delete(_artifactRoot, recursive: true);
            }
        }

        private static async Task WaitUntilHealthyAsync(
            string containerName,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var state = await RunDockerAsync(
                    ["container", "inspect", "--format", "{{.State.Health.Status}}", containerName],
                    cancellationToken);
                if (state.ExitCode == 0 && string.Equals(state.StandardOutput.Trim(), "healthy", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            var stateResult = await RunDockerAsync(
                ["container", "inspect", "--format", "{{json .State}}", containerName],
                cancellationToken);
            var logsResult = await RunDockerAsync(["container", "logs", containerName], cancellationToken);
            throw new InvalidOperationException(
                "Managed PostgreSQL did not become healthy within 60 seconds. " +
                $"state={stateResult.StandardOutput.Trim()} logs={logsResult.StandardOutput.Trim()} " +
                $"stderr={logsResult.StandardError.Trim()}");
        }

        private static async Task<int> GetPublishedPortAsync(
            string containerName,
            CancellationToken cancellationToken)
        {
            var result = await RunDockerAsync(["port", containerName, "5432/tcp"], cancellationToken);
            if (result.ExitCode != 0)
            {
                var inspection = await RunDockerAsync(
                    ["container", "inspect", "--format", "{{json .HostConfig.PortBindings}}", containerName],
                    cancellationToken);
                throw new InvalidOperationException(
                    $"Docker could not resolve the dynamic PostgreSQL port (exit {result.ExitCode}): " +
                    $"{result.StandardError.Trim()}; bindings={inspection.StandardOutput.Trim()}");
            }

            var endpoint = result.StandardOutput.Trim();
            var separator = endpoint.LastIndexOf(':');
            if (separator < 0 ||
                !int.TryParse(endpoint[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                throw new InvalidOperationException("Docker returned an invalid PostgreSQL port mapping.");
            }

            return port;
        }

        private static async Task EnsureDockerSuccessAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            EnsureSuccess("provision managed PostgreSQL", await RunDockerAsync(arguments, cancellationToken));
        }

        private static void EnsureSuccess(string operation, DockerCommandResult result)
        {
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Docker could not {operation} (exit {result.ExitCode}): {result.StandardError.Trim()}");
            }
        }

        private static async Task<DockerCommandResult> RunDockerAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FindDockerExecutable(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Docker CLI did not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new DockerCommandResult(process.ExitCode, await standardOutput, await standardError);
        }

        private static string FindDockerExecutable()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, "docker");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException("Docker CLI was not found on PATH.");
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Repository root was not found.");
        }

        private sealed record DockerCommandResult(int ExitCode, string StandardOutput, string StandardError);
    }
}
