namespace Harness.ArchitectureTests;

public sealed class ServerModeInfrastructureTests
{
    [Fact]
    public void ProductionComposeProvidesPostgresArtifactsBackupsAndHardenedHost()
    {
        var compose = ReadRepositoryFile("infra/compose/production.compose.yaml");

        Assert.Contains("postgres:", compose, StringComparison.Ordinal);
        Assert.Contains("minio:", compose, StringComparison.Ordinal);
        Assert.Contains("host:", compose, StringComparison.Ordinal);
        Assert.Contains("postgres-basebackup:", compose, StringComparison.Ordinal);
        Assert.Contains("archive_mode=on", compose, StringComparison.Ordinal);
        Assert.Contains("archive_timeout=300", compose, StringComparison.Ordinal);
        Assert.Contains("secret://database_connection_string", compose, StringComparison.Ordinal);
        Assert.Contains("read_only: true", compose, StringComparison.Ordinal);
        Assert.Contains("no-new-privileges:true", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0:", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void VaultIsOptionalAndUsesAppRoleFilesAndRenderedSecrets()
    {
        var compose = ReadRepositoryFile("infra/compose/vault.compose.yaml");
        var agent = ReadRepositoryFile("infra/vault/agent.hcl");
        var template = ReadRepositoryFile(
            "infra/vault/database_connection_string.ctmpl");

        Assert.Contains("profiles: [\"vault\"]", compose, StringComparison.Ordinal);
        Assert.Contains("vault_role_id", compose, StringComparison.Ordinal);
        Assert.Contains("vault_secret_id", compose, StringComparison.Ordinal);
        Assert.Contains("method \"approle\"", agent, StringComparison.Ordinal);
        Assert.Contains("/vault/rendered/database_connection_string", agent, StringComparison.Ordinal);
        Assert.Contains("kv/data/poseidon/server", template, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres_connection =", template, StringComparison.Ordinal);
    }

    [Fact]
    public void AssistedMigrationNeverAcceptsConnectionStringAsArgument()
    {
        var script = ReadRepositoryFile("tools/backend/migrate-to-server.sh");

        Assert.Contains("POSEIDON_POSTGRES_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("--confirm", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--connection", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantRlsMigrationCreatesUsingAndCheckPolicies()
    {
        var migration = ReadRepositoryFile(
            "src/Harness.Persistence.Postgres/Migrations/0080_tenant_row_level_security.sql");

        Assert.Contains("ENABLE ROW LEVEL SECURITY", migration, StringComparison.Ordinal);
        Assert.Contains("CREATE POLICY tenant_isolation", migration, StringComparison.Ordinal);
        Assert.Contains("current_setting(''poseidon.tenant_id'', true)", migration, StringComparison.Ordinal);
        Assert.Contains("WITH CHECK", migration, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
        return File.ReadAllText(Path.Combine(root, relativePath));
    }
}
