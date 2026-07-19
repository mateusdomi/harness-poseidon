using Harness.Persistence.Postgres;
using Npgsql;

namespace Harness.Host.Persistence;

public sealed class PostgresMigrationHostedService(
    NpgsqlDataSource dataSource,
    ILogger<PostgresMigrationHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, Exception?> PersistenceReady =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1004, nameof(PersistenceReady)),
            "PostgreSQL persistence is ready; {MigrationCount} migration(s) applied.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var applied = await PostgresMigrationRunner.ApplyAsync(dataSource, cancellationToken);
        PersistenceReady(logger, applied, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
