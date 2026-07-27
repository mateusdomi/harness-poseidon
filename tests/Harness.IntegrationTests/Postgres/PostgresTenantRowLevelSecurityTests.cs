using Harness.Persistence.Postgres;
using Npgsql;

namespace Harness.IntegrationTests.Postgres;

[Collection("managed-postgres")]
public sealed class PostgresTenantRowLevelSecurityTests
{
    private const string TenantOne = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantTwo = "01BRZ3NDEKTSV4RRFFQ69G5T00";

    [Fact]
    public async Task RestrictedRoleSeesAndWritesOnlyTheConfiguredTenant()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var fixture = await PostgresSkipLockedPocTests.ManagedPostgresFixture
            .StartAsync(timeout.Token);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        Assert.True(await PostgresMigrationRunner.ApplyAsync(dataSource, timeout.Token) >= 73);
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token);
        var role = $"poseidon_rls_probe_{Guid.NewGuid():N}";

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.tenants (id,name,created_at)
            VALUES
                ($1,'Tenant one',now()),
                ($2,'Tenant two',now());
            """,
            timeout.Token,
            TenantOne,
            TenantTwo);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.organizations (id,tenant_id,name,created_at)
            VALUES
                ('01ARZ3NDEKTSV4RRFFQ69G5FAW',$1,'One',now()),
                ('01BRZ3NDEKTSV4RRFFQ69G5T01',$2,'Two',now());
            """,
            timeout.Token,
            TenantOne,
            TenantTwo);

        await using (var coverage = connection.CreateCommand())
        {
            coverage.Transaction = transaction;
            coverage.CommandText =
                """
                SELECT COUNT(*)
                FROM information_schema.columns c
                JOIN pg_class t ON t.relname=c.table_name
                JOIN pg_namespace n ON n.oid=t.relnamespace AND n.nspname=c.table_schema
                WHERE c.table_schema='harness'
                  AND c.column_name='tenant_id'
                  AND t.relrowsecurity
                  AND t.relforcerowsecurity
                  AND EXISTS
                  (
                      SELECT 1 FROM pg_policies p
                      WHERE p.schemaname=c.table_schema
                        AND p.tablename=c.table_name
                        AND p.policyname='tenant_isolation'
                  );
                """;
            var protectedCount = (long)(await coverage.ExecuteScalarAsync(timeout.Token))!;

            coverage.CommandText =
                """
                SELECT COUNT(DISTINCT table_name)
                FROM information_schema.columns
                WHERE table_schema='harness' AND column_name='tenant_id';
                """;
            var tenantTableCount = (long)(await coverage.ExecuteScalarAsync(timeout.Token))!;
            Assert.Equal(tenantTableCount, protectedCount);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"""
            CREATE ROLE "{role}" NOLOGIN;
            GRANT USAGE ON SCHEMA harness TO "{role}";
            GRANT SELECT, INSERT ON harness.organizations TO "{role}";
            SET LOCAL ROLE "{role}";
            """,
            timeout.Token);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT set_config('poseidon.tenant_id',$1,true);",
            timeout.Token,
            TenantOne);

        await using (var visible = connection.CreateCommand())
        {
            visible.Transaction = transaction;
            visible.CommandText = "SELECT tenant_id FROM harness.organizations ORDER BY tenant_id;";
            Assert.Equal(TenantOne, (string?)await visible.ExecuteScalarAsync(timeout.Token));
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO harness.organizations (id,tenant_id,name,created_at)
                VALUES ('01BRZ3NDEKTSV4RRFFQ69G5T02',$1,'Denied',now());
                """,
                timeout.Token,
                TenantTwo));
        Assert.Equal("42501", denied.SqlState);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params string[] values)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var index = 0; index < values.Length; index++)
        {
            command.Parameters.AddWithValue(values[index]);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
