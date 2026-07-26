using System.Text.Json;
using Harness.Persistence.Migration;
using Npgsql;

try
{
    var options = AssistedMigrationOptions.Parse(args);
    var connectionString = Environment.GetEnvironmentVariable(
        AssistedMigrationOptions.ConnectionEnvironmentVariable);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            $"{AssistedMigrationOptions.ConnectionEnvironmentVariable} não está configurada.");
    }

    await using var target = NpgsqlDataSource.Create(connectionString);
    var report = await SqliteToPostgresMigrator.MigrateAsync(
        options.SqliteDatabasePath,
        target);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "completed",
        tablesCovered = report.TablesCovered,
        sourceRows = report.TotalSourceRows,
        inserted = report.TotalInserted,
        skippedExisting = report.TotalSkippedExisting,
        deferred = report.Deferred.Count,
    }));
    return 0;
}
catch (Exception exception) when (
    exception is ArgumentException or
    InvalidOperationException or
    IOException or
    NpgsqlException)
{
    Console.Error.WriteLine($"migration_failed:{exception.GetType().Name}");
    return 2;
}
