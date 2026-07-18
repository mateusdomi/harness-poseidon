using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteWriteDispatcher : IAsyncDisposable
{
    private const int BusyTimeoutMilliseconds = 5_000;
    private readonly SqliteConnection _connection;
    private readonly Channel<IWriteRequest> _requests;
    private readonly Task _worker;
    private int _disposed;

    private SqliteWriteDispatcher(SqliteConnection connection)
    {
        _connection = connection;
        _requests = Channel.CreateUnbounded<IWriteRequest>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = ProcessRequestsAsync();
    }

    public static async Task<SqliteWriteDispatcher> CreateAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Database path must include a directory.", nameof(databasePath));
        Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            return new SqliteWriteDispatcher(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var request = new WriteRequest<T>(operation, cancellationToken);
        if (!_requests.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("The SQLite write dispatcher is not accepting requests.");
        }

        return request.Completion;
    }

    public Task ExecuteAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteAsync(
            async (connection, token) =>
            {
                await operation(connection, token);
                return true;
            },
            cancellationToken);
    }

    public Task<SqlitePragmaState> ReadPragmaStateAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            async (connection, token) =>
            {
                var journalMode = await ExecuteScalarAsync(connection, "PRAGMA journal_mode;", token);
                var foreignKeys = await ExecuteScalarAsync(connection, "PRAGMA foreign_keys;", token);
                var busyTimeout = await ExecuteScalarAsync(connection, "PRAGMA busy_timeout;", token);

                return new SqlitePragmaState(
                    Convert.ToString(journalMode, CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToInt64(foreignKeys, CultureInfo.InvariantCulture) == 1,
                    Convert.ToInt32(busyTimeout, CultureInfo.InvariantCulture));
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.Writer.TryComplete();
        await _worker;
        await _connection.DisposeAsync();
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteScalarAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            $"PRAGMA busy_timeout={BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};",
            cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The dispatcher must transfer every operation failure to its request without terminating the single reader.")]
    private async Task ProcessRequestsAsync()
    {
        await foreach (var request in _requests.Reader.ReadAllAsync())
        {
            await request.ExecuteAsync(_connection);
        }
    }

    private interface IWriteRequest
    {
        Task ExecuteAsync(SqliteConnection connection);
    }

    private sealed class WriteRequest<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) : IWriteRequest
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public async Task ExecuteAsync(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                var result = await operation(connection, cancellationToken);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
