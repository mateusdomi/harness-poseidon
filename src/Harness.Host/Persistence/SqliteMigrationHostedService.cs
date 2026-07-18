using Harness.Persistence.Sqlite;

namespace Harness.Host.Persistence;

public sealed class SqliteMigrationHostedService(
    SqliteWriteDispatcher dispatcher,
    ILogger<SqliteMigrationHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, Exception?> PersistenceReady =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1001, nameof(PersistenceReady)),
            "SQLite persistence is ready; {MigrationCount} migration(s) applied.");

    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly ILogger<SqliteMigrationHostedService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var applied = await SqliteMigrationRunner.ApplyAsync(_dispatcher, cancellationToken);
        PersistenceReady(_logger, applied, null);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
