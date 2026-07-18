using System.Globalization;
using Harness.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Harness.ConcurrencyTests.Sqlite;

public sealed class SqliteWriteDispatcherPocTests
{
    [Fact]
    public async Task ConcurrentProducersAreSerializedWithoutBusyFailures()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var artifactDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "poc-1",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var databasePath = Path.Combine(artifactDirectory, "dispatcher.db");

        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, timeout.Token);
            await CreateSchemaAsync(dispatcher, timeout.Token);

            var activeOperations = 0;
            var maximumActiveOperations = 0;
            const int producerCount = 24;
            const int writesPerProducer = 40;

            var producers = Enumerable.Range(0, producerCount).Select(
                producer => ProduceWritesAsync(
                    dispatcher,
                    producer,
                    writesPerProducer,
                    () =>
                    {
                        var active = Interlocked.Increment(ref activeOperations);
                        UpdateMaximum(ref maximumActiveOperations, active);
                    },
                    () => Interlocked.Decrement(ref activeOperations),
                    timeout.Token));

            await Task.WhenAll(producers);

            var persistedCount = await CountRowsAsync(dispatcher, timeout.Token);
            var pragmaState = await dispatcher.ReadPragmaStateAsync(timeout.Token);

            Assert.Equal(producerCount * writesPerProducer, persistedCount);
            Assert.Equal(1, maximumActiveOperations);
            Assert.Equal("wal", pragmaState.JournalMode, ignoreCase: true);
            Assert.True(pragmaState.ForeignKeysEnabled);
            Assert.Equal(5_000, pragmaState.BusyTimeoutMilliseconds);
        }
        finally
        {
            if (Directory.Exists(artifactDirectory))
            {
                Directory.Delete(artifactDirectory, recursive: true);
            }
        }
    }

    private static Task CreateSchemaAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        return dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE poc_work_items (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        producer INTEGER NOT NULL,
                        sequence INTEGER NOT NULL,
                        payload TEXT NOT NULL,
                        UNIQUE (producer, sequence)
                    );
                    """;
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
    }

    private static async Task ProduceWritesAsync(
        SqliteWriteDispatcher dispatcher,
        int producer,
        int writeCount,
        Action operationStarted,
        Action operationFinished,
        CancellationToken cancellationToken)
    {
        for (var sequence = 0; sequence < writeCount; sequence++)
        {
            var capturedSequence = sequence;
            await dispatcher.ExecuteAsync(
                async (connection, token) =>
                {
                    operationStarted();
                    try
                    {
                        await Task.Yield();
                        await InsertAsync(connection, producer, capturedSequence, token);
                    }
                    finally
                    {
                        operationFinished();
                    }
                },
                cancellationToken);
        }
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        int producer,
        int sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO poc_work_items (producer, sequence, payload)
            VALUES ($producer, $sequence, $payload);
            """;
        command.Parameters.AddWithValue("$producer", producer);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$payload", $"producer-{producer}-write-{sequence}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task<long> CountRowsAsync(
        SqliteWriteDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        return dispatcher.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM poc_work_items;";
                var count = await command.ExecuteScalarAsync(token);
                return Convert.ToInt64(count, CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
