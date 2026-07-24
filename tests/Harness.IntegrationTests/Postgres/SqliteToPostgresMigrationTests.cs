using System.Globalization;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Migration;
using Harness.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Harness.IntegrationTests.Postgres;

/// <summary>
/// Dual-provider proof that <see cref="SqliteToPostgresMigrator"/> moves a representative
/// personal-mode SQLite dataset — multiple tenants, non-zero optimistic-concurrency versions, a
/// self-referencing document-version chain, and multi-link audit ledgers — into a managed
/// PostgreSQL container while preserving row counts, OCC versions, the audit hash chains, and
/// idempotency.
/// </summary>
[Collection("managed-postgres")]
public sealed class SqliteToPostgresMigrationTests
{
    private const string Tenant1 = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization1 = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Project1 = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string User1 = "01ARZ3NDEKTSV4RRFFQ69G5FAY";

    private const string Tenant2 = "01BRZ3NDEKTSV4RRFFQ69G5T00";
    private const string Organization2 = "01BRZ3NDEKTSV4RRFFQ69G5T01";
    private const string Project2 = "01BRZ3NDEKTSV4RRFFQ69G5T02";
    private const string User2 = "01BRZ3NDEKTSV4RRFFQ69G5T03";
    private const string Solicitation2 = "01BRZ3NDEKTSV4RRFFQ69G5T04";
    private const string Demand2 = "01BRZ3NDEKTSV4RRFFQ69G5T05";

    [Fact]
    public async Task MigratesEveryDomainTablePreservingVersionsLedgerAndIdempotency()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "integration-artifacts",
            $"sqlite-to-postgres-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "personal.db");

        try
        {
            await SeedSqliteAsync(databasePath, timeout.Token);

            await using var fixture = await PostgresSkipLockedPocTests.ManagedPostgresFixture
                .StartAsync(timeout.Token);
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);

            var report = await SqliteToPostgresMigrator.MigrateAsync(
                databasePath, dataSource, timeout.Token);

            // Every PostgreSQL base table is accounted for, none deferred.
            Assert.Empty(report.Deferred);
            Assert.Equal(await CountBaseTablesAsync(dataSource, timeout.Token), report.TablesCovered);
            Assert.True(report.TotalInserted > 0);

            // Row counts match per table; every source row is either inserted or recognised as
            // already present (catalog rows the PostgreSQL migrations seed themselves).
            foreach (var table in report.Tables)
            {
                var postgresCount = await CountRowsAsync(dataSource, table.Table, timeout.Token);
                Assert.Equal(table.SourceRows, postgresCount);
                Assert.Equal(table.SourceRows, table.Inserted + table.SkippedExisting);
            }

            // Domain data was copied fresh; migration-seeded catalog rows were skipped, not doubled.
            Assert.Equal(2, TableFor(report, "tenants").Inserted);
            Assert.Equal(
                Harness.Host.Agents.CanonicalAgentDefinitions.All.Count,
                TableFor(report, "agent_definitions").SkippedExisting);
            Assert.Equal(0, TableFor(report, "agent_definitions").Inserted);

            // Spot counts across the covered surface.
            Assert.Equal(2, await CountRowsAsync(dataSource, "tenants", timeout.Token));
            Assert.Equal(2, await CountRowsAsync(dataSource, "projects", timeout.Token));
            Assert.Equal(2, await CountRowsAsync(dataSource, "demands", timeout.Token));
            Assert.Equal(2, await CountRowsAsync(dataSource, "document_versions", timeout.Token));
            Assert.Equal(5, await CountRowsAsync(dataSource, "audit_ledger", timeout.Token));

            // Optimistic-concurrency versions preserved verbatim.
            Assert.Equal(2L, await ScalarLongAsync(dataSource, "tenants", "version", "id", Tenant1, timeout.Token));
            Assert.Equal(3L, await ScalarLongAsync(dataSource, "tenants", "version", "id", Tenant2, timeout.Token));
            Assert.Equal(4L, await ScalarLongAsync(dataSource, "projects", "version", "id", Project1, timeout.Token));
            Assert.Equal(7L, await ScalarLongAsync(dataSource, "local_users", "version", "id", User1, timeout.Token));
            Assert.Equal(4L, await ScalarLongAsync(dataSource, "documents", "version", "id", "01ARZ3NDEKTSV4RRFFQ69G5FH0", timeout.Token));

            // Tenant-scoped payloads reload identically.
            Assert.Equal(
                "oidc-subject-1",
                await ScalarStringAsync(dataSource, "local_users", "external_subject", "id", User1, timeout.Token));
            Assert.Equal(
                1L,
                await CountByTenantAsync(dataSource, "demands", Tenant2, timeout.Token));

            // The self-referencing document-version chain survived and links v2 -> v1.
            Assert.Equal(
                "01ARZ3NDEKTSV4RRFFQ69G5FH1",
                await ScalarStringAsync(
                    dataSource, "document_versions", "supersedes_id", "id",
                    "01ARZ3NDEKTSV4RRFFQ69G5FH5", timeout.Token));

            // The audit ledger hash chain re-verifies on PostgreSQL after jsonb normalization.
            await AssertLedgerChainVerifiesAsync(dataSource, timeout.Token);

            // Re-running is a no-op: nothing inserted, everything skipped, counts unchanged.
            var second = await SqliteToPostgresMigrator.MigrateAsync(
                databasePath, dataSource, timeout.Token);
            Assert.Equal(0, second.TotalInserted);
            foreach (var table in second.Tables)
            {
                Assert.Equal(0, table.Inserted);
                Assert.Equal(table.SourceRows, table.SkippedExisting);
                Assert.Equal(table.SourceRows, await CountRowsAsync(dataSource, table.Table, timeout.Token));
            }

            await AssertLedgerChainVerifiesAsync(dataSource, timeout.Token);

            // A matching primary key with divergent content is corruption, not an idempotent
            // replay. The migrator must fail closed and must not disclose the row values.
            await ExecutePostgresAsync(
                dataSource,
                $"UPDATE harness.tenants SET name = 'Conflicting target' WHERE id = '{Tenant1}';",
                timeout.Token);
            var conflict = await Assert.ThrowsAsync<MigrationDataConflictException>(() =>
                SqliteToPostgresMigrator.MigrateAsync(databasePath, dataSource, timeout.Token));
            Assert.Equal("tenants", conflict.TableName);
            Assert.DoesNotContain(Tenant1, conflict.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static TableMigrationResult TableFor(MigrationReport report, string table) =>
        report.Tables.Single(entry => string.Equals(entry.Table, table, StringComparison.Ordinal));

    private static async Task SeedSqliteAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, cancellationToken);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);

        await ExecuteAsync(dispatcher, FoundationSeedSql, cancellationToken);
        await ExecuteAsync(dispatcher, WorkChainInsertSql, cancellationToken);
        await ExecuteAsync(dispatcher, WorkflowInsertSql, cancellationToken);
        await ExecuteAsync(dispatcher, DocumentSeedSql, cancellationToken);
        await ExecuteAsync(dispatcher, TenantTwoSeedSql, cancellationToken);
        await ExecuteAsync(dispatcher, MessagingSeedSql, cancellationToken);

        await InsertLedgerAsync(dispatcher, Tenant1,
        [
            new LedgerEvent("tenant.provisioned", "{\"z\":1,\"a\":{\"n\":2,\"m\":1},\"k\":[3,2,1]}", "2026-07-18T13:30:00Z"),
            new LedgerEvent("project.created", "{\"projectId\":\"" + Project1 + "\",\"nested\":{\"b\":2,\"a\":1}}", "2026-07-18T13:31:00Z"),
            new LedgerEvent("task.created", "{\"weight\":3,\"criteria\":[\"green\",\"reviewed\"]}", "2026-07-18T13:32:00Z"),
        ], cancellationToken);
        await InsertLedgerAsync(dispatcher, Tenant2,
        [
            new LedgerEvent("tenant.provisioned", "{\"region\":\"eu\",\"flags\":{\"beta\":true}}", "2026-07-18T14:00:00Z"),
            new LedgerEvent("demand.created", "{\"demandId\":\"" + Demand2 + "\",\"priority\":\"high\"}", "2026-07-18T14:01:00Z"),
        ], cancellationToken);
    }

    private sealed record LedgerEvent(string EventType, string PayloadJson, string OccurredAt);

    private static async Task InsertLedgerAsync(
        SqliteWriteDispatcher dispatcher,
        string tenantId,
        IReadOnlyList<LedgerEvent> events,
        CancellationToken cancellationToken)
    {
        var previousHash = AuditLedgerHash.Genesis;
        for (var index = 0; index < events.Count; index++)
        {
            var occurredAt = DateTimeOffset.Parse(
                events[index].OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var sequence = index + 1;
            var eventHash = AuditLedgerHash.Compute(
                previousHash, tenantId, sequence, events[index].EventType, events[index].PayloadJson, occurredAt);
            var id = $"{tenantId[..20]}LG{sequence:D4}";

            var capturedPrevious = previousHash;
            var capturedEvent = events[index];
            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO audit_ledger
                            (id, tenant_id, sequence, previous_hash, event_hash, event_type,
                             payload_json, occurred_at)
                        VALUES ($id, $tenant, $sequence, $previous, $hash, $type, $payload, $at);
                        """;
                    command.Parameters.AddWithValue("$id", id);
                    command.Parameters.AddWithValue("$tenant", tenantId);
                    command.Parameters.AddWithValue("$sequence", sequence);
                    command.Parameters.AddWithValue("$previous", capturedPrevious);
                    command.Parameters.AddWithValue("$hash", eventHash);
                    command.Parameters.AddWithValue("$type", capturedEvent.EventType);
                    command.Parameters.AddWithValue("$payload", capturedEvent.PayloadJson);
                    command.Parameters.AddWithValue(
                        "$at", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken);

            previousHash = eventHash;
        }
    }

    private static async Task AssertLedgerChainVerifiesAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT tenant_id, sequence, previous_hash, event_hash, event_type,
                   payload_json::text, occurred_at
            FROM harness.audit_ledger
            ORDER BY tenant_id, sequence;
            """);

        var expectedPrevious = new Dictionary<string, string>(StringComparer.Ordinal);
        var verified = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var tenantId = reader.GetString(0);
            var sequence = reader.GetInt64(1);
            var previousHash = reader.GetString(2);
            var eventHash = reader.GetString(3);
            var eventType = reader.GetString(4);
            var payloadJson = reader.GetString(5);
            var occurredAt = reader.GetFieldValue<DateTimeOffset>(6);

            var priorHash = expectedPrevious.GetValueOrDefault(tenantId, AuditLedgerHash.Genesis);
            Assert.Equal(priorHash, previousHash);

            var recomputed = AuditLedgerHash.Compute(
                previousHash, tenantId, sequence, eventType, payloadJson, occurredAt);
            Assert.Equal(eventHash, recomputed);

            expectedPrevious[tenantId] = eventHash;
            verified++;
        }

        Assert.Equal(5, verified);
    }

    private static async Task<int> CountBaseTablesAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'harness' AND c.relkind = 'r' AND c.relname <> 'schema_migrations';
            """);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRowsAsync(
        NpgsqlDataSource dataSource,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT COUNT(*) FROM harness.\"{table}\";");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountByTenantAsync(
        NpgsqlDataSource dataSource,
        string table,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT COUNT(*) FROM harness.\"{table}\" WHERE tenant_id = $1;");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tenantId });
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<long> ScalarLongAsync(
        NpgsqlDataSource dataSource,
        string table,
        string column,
        string keyColumn,
        string keyValue,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT \"{column}\" FROM harness.\"{table}\" WHERE \"{keyColumn}\" = $1;");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = keyValue });
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        NpgsqlDataSource dataSource,
        string table,
        string column,
        string keyColumn,
        string keyValue,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT \"{column}\" FROM harness.\"{table}\" WHERE \"{keyColumn}\" = $1;");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = keyValue });
        return (string)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Expected a non-null value."));
    }

    private static async Task ExecuteAsync(
        SqliteWriteDispatcher dispatcher,
        string sql,
        CancellationToken cancellationToken)
    {
        await dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            },
                cancellationToken);
    }

    private static async Task ExecutePostgresAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string FoundationSeedSql =
        """
        INSERT INTO tenants (id, name, version, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Tenant One', 2, '2026-07-18T13:30:00.0000000+00:00');
        INSERT INTO organizations (id, tenant_id, name, version, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAW', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Org One', 1,
                '2026-07-18T13:30:00.0000000+00:00');
        INSERT INTO projects (id, tenant_id, organization_id, name, version, created_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAX', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                '01ARZ3NDEKTSV4RRFFQ69G5FAW', 'Project One', 4, '2026-07-18T13:30:00.0000000+00:00');
        INSERT INTO local_users (id, tenant_id, display_name, version, created_at, role, external_subject)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAY', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'Local User', 7,
                '2026-07-18T13:30:00.0000000+00:00', 'admin', 'oidc-subject-1');
        """;

    private const string TenantTwoSeedSql =
        """
        INSERT INTO tenants (id, name, version, created_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T00', 'Tenant Two', 3, '2026-07-18T14:00:00.0000000+00:00');
        INSERT INTO organizations (id, tenant_id, name, version, created_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T01', '01BRZ3NDEKTSV4RRFFQ69G5T00', 'Org Two', 1,
                '2026-07-18T14:00:00.0000000+00:00');
        INSERT INTO projects (id, tenant_id, organization_id, name, version, created_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T02', '01BRZ3NDEKTSV4RRFFQ69G5T00',
                '01BRZ3NDEKTSV4RRFFQ69G5T01', 'Project Two', 1, '2026-07-18T14:00:00.0000000+00:00');
        INSERT INTO local_users (id, tenant_id, display_name, version, created_at, role, external_subject)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T03', '01BRZ3NDEKTSV4RRFFQ69G5T00', 'User Two', 1,
                '2026-07-18T14:00:00.0000000+00:00', 'member', 'oidc-subject-2');
        INSERT INTO solicitations (id, tenant_id, project_id, user_id, content, created_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T04', '01BRZ3NDEKTSV4RRFFQ69G5T00',
                '01BRZ3NDEKTSV4RRFFQ69G5T02', '01BRZ3NDEKTSV4RRFFQ69G5T03', 'Second request',
                '2026-07-18T14:00:00.0000000+00:00');
        INSERT INTO demands
            (id, tenant_id, project_id, solicitation_id, title, acceptance_criteria_json, created_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T05', '01BRZ3NDEKTSV4RRFFQ69G5T00',
                '01BRZ3NDEKTSV4RRFFQ69G5T02', '01BRZ3NDEKTSV4RRFFQ69G5T04', 'Second demand',
                '["shipped"]', '2026-07-18T14:00:00.0000000+00:00');
        """;

    private const string MessagingSeedSql =
        """
        INSERT INTO inbox_messages (tenant_id, idempotency_key, message_hash, response_json, processed_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'seed-key-1',
                'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
                '{"ok":true}', '2026-07-18T13:30:00.0000000+00:00');
        INSERT INTO inbox_messages (tenant_id, idempotency_key, message_hash, response_json, processed_at)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T00', 'seed-key-2',
                'CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC',
                '{"ok":true,"tenant":"two"}', '2026-07-18T14:00:00.0000000+00:00');
        INSERT INTO outbox_messages (id, tenant_id, event_type, payload_json, occurred_at, attempts)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5OB0', '01ARZ3NDEKTSV4RRFFQ69G5FAV', 'project.created',
                '{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAX"}', '2026-07-18T13:31:00.0000000+00:00', 0);
        INSERT INTO outbox_messages (id, tenant_id, event_type, payload_json, occurred_at, attempts)
        VALUES ('01BRZ3NDEKTSV4RRFFQ69G5OB1', '01BRZ3NDEKTSV4RRFFQ69G5T00', 'demand.created',
                '{"demandId":"01BRZ3NDEKTSV4RRFFQ69G5T05"}', '2026-07-18T14:01:00.0000000+00:00', 1);
        INSERT INTO realtime_streams (tenant_id, stream_name, last_sequence, created_at, updated_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5FAV', 'project:01ARZ3NDEKTSV4RRFFQ69G5FAX', 1,
                '2026-07-18T17:30:00.0000000+00:00', '2026-07-18T17:30:00.0000000+00:00');
        INSERT INTO realtime_events
            (message_id, tenant_id, stream_name, sequence, event_type, payload_json, occurred_at)
        VALUES ('01ARZ3NDEKTSV4RRFFQ69G5RE0', '01ARZ3NDEKTSV4RRFFQ69G5FAV',
                'project:01ARZ3NDEKTSV4RRFFQ69G5FAX', 1, 'task.created', '{}',
                '2026-07-18T17:30:00.0000000+00:00');
        """;

    private const string DocumentSeedSql =
        """
        INSERT INTO documents
            (id,tenant_id,project_id,title,kind,state,current_version,
             phase_name,inconsistent,version,created_at,updated_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FH0','01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX','Delivery spec','spec',
             'awaiting_approval',2,NULL,0,4,
             '2026-07-18T17:00:00.0000000+00:00','2026-07-18T17:02:00.0000000+00:00');
        INSERT INTO document_versions
            (id,tenant_id,project_id,document_id,version,catalog_path,
             content_hash,supersedes_id,author_kind,author_id,created_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FH1','01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',1,
             'documents/spec-v1.md',
             'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
             NULL,'agent','01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:00:00.0000000+00:00');
        INSERT INTO document_versions
            (id,tenant_id,project_id,document_id,version,catalog_path,
             content_hash,supersedes_id,author_kind,author_id,created_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FH5','01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',2,
             'documents/spec-v2.md',
             'BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB',
             '01ARZ3NDEKTSV4RRFFQ69G5FH1','agent','01ARZ3NDEKTSV4RRFFQ69G5FAY',
             '2026-07-18T17:01:00.0000000+00:00');
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
             '01ARZ3NDEKTSV4RRFFQ69G5FH5','Approve spec','Review current version',
             'high','2026-07-20T17:00:00.0000000+00:00','pending',
             '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00.0000000+00:00');
        INSERT INTO document_state_transitions
            (id,tenant_id,project_id,document_id,document_version,
             from_state,to_state,actor_kind,actor_id,occurred_at)
        VALUES
            ('01ARZ3NDEKTSV4RRFFQ69G5FH3','01ARZ3NDEKTSV4RRFFQ69G5FAV',
             '01ARZ3NDEKTSV4RRFFQ69G5FAX','01ARZ3NDEKTSV4RRFFQ69G5FH0',4,
             'in_review','awaiting_approval','agent',
             '01ARZ3NDEKTSV4RRFFQ69G5FAY','2026-07-18T17:02:00.0000000+00:00');
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
}
