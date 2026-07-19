using Microsoft.Data.Sqlite;
using Npgsql;

namespace Harness.Persistence.Migration;

/// <summary>
/// Migrates every domain table from a personal-mode SQLite database into a server-mode PostgreSQL
/// database, preserving tenant isolation, optimistic-concurrency <c>version</c> columns, the
/// append-only audit-ledger hash chains, Inbox/Outbox, and idempotency records.
/// </summary>
/// <remarks>
/// <para>
/// The migrator is schema-introspection driven: it reads the live <c>harness</c> schema from
/// <c>pg_catalog</c>, so it always covers exactly the tables the target migrations created — never
/// a hand-maintained list that could drift. Tables are copied in foreign-key topological order, and
/// rows inside self-referencing tables (for example <c>document_versions.supersedes_id</c>) are
/// ordered so a parent is always inserted before the row that supersedes it.
/// </para>
/// <para>
/// Idempotency: every row is inserted with <c>ON CONFLICT (&lt;primary key&gt;) DO NOTHING</c>, and
/// the whole copy runs in a single transaction, so re-running the migration is a safe no-op.
/// </para>
/// <para>
/// The append-only triggers on the ledger and document history reject <c>UPDATE</c>/<c>DELETE</c>
/// only — <c>INSERT</c> is allowed — so the hash chains are copied verbatim and remain verifiable.
/// File-backed content catalogs (document bodies, attachment/reference assets) are stored on disk,
/// not in SQL; the migrator copies their metadata rows (catalog path + SHA-256) verbatim, and any
/// file-tree copy must be performed separately as an explicit operational step.
/// </para>
/// </remarks>
public static class SqliteToPostgresMigrator
{
    /// <summary>
    /// Applies the PostgreSQL migrations to <paramref name="target"/> (bringing the schema to head),
    /// then copies all domain data from the SQLite database at <paramref name="sqliteDatabasePath"/>.
    /// </summary>
    public static async Task<MigrationReport> MigrateAsync(
        string sqliteDatabasePath,
        NpgsqlDataSource target,
        CancellationToken cancellationToken = default) =>
        await MigrateAsync(sqliteDatabasePath, target, applyTargetMigrations: true, cancellationToken);

    /// <summary>
    /// Copies all domain data from the SQLite database into <paramref name="target"/>. When
    /// <paramref name="applyTargetMigrations"/> is <see langword="true"/> the PostgreSQL schema is
    /// migrated to head first; otherwise the caller guarantees the schema is already at head.
    /// </summary>
    public static async Task<MigrationReport> MigrateAsync(
        string sqliteDatabasePath,
        NpgsqlDataSource target,
        bool applyTargetMigrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteDatabasePath);
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(sqliteDatabasePath))
        {
            throw new FileNotFoundException("SQLite source database was not found.", sqliteDatabasePath);
        }

        if (applyTargetMigrations)
        {
            await Harness.Persistence.Postgres.PostgresMigrationRunner.ApplyAsync(target, cancellationToken);
        }

        await using var source = OpenSqlite(sqliteDatabasePath);
        await source.OpenAsync(cancellationToken);

        await using var connection = await target.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var tables = await PostgresSchemaReader.ReadAsync(connection, transaction, cancellationToken);
        var results = new List<TableMigrationResult>();
        foreach (var table in tables)
        {
            var sqliteColumns = await ReadSqliteColumnsAsync(source, table.Name, cancellationToken);
            if (sqliteColumns.Count == 0)
            {
                throw new MigrationSchemaException(
                    $"Table 'harness.{table.Name}' is absent from the SQLite source database. " +
                    "Upgrade the SQLite source to the current schema before migrating data.");
            }

            var copyColumns = table.Columns
                .Where(column => sqliteColumns.Contains(column.Name))
                .ToArray();
            EnsureCopyable(table, sqliteColumns);

            var result = await CopyTableAsync(
                source, connection, transaction, table, copyColumns, cancellationToken);
            results.Add(result);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MigrationReport(results, []);
    }

    private static void EnsureCopyable(
        PgTable table,
        IReadOnlySet<string> sqliteColumns)
    {
        var missingRequired = table.Columns
            .Where(column => column.IsNotNull && !column.HasDefault && !sqliteColumns.Contains(column.Name))
            .Select(column => column.Name)
            .ToArray();
        if (missingRequired.Length > 0)
        {
            throw new MigrationSchemaException(
                $"Table 'harness.{table.Name}' requires columns absent from SQLite: " +
                string.Join(", ", missingRequired));
        }

        var missingKey = table.PrimaryKey.Where(key => !sqliteColumns.Contains(key)).ToArray();
        if (missingKey.Length > 0)
        {
            throw new MigrationSchemaException(
                $"Table 'harness.{table.Name}' primary-key columns absent from SQLite: " +
                string.Join(", ", missingKey));
        }

        var postgresColumns = table.Columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var unmappedSource = sqliteColumns
            .Where(column => !postgresColumns.Contains(column))
            .OrderBy(column => column, StringComparer.Ordinal)
            .ToArray();
        if (unmappedSource.Length > 0)
        {
            throw new MigrationSchemaException(
                $"SQLite table '{table.Name}' has columns absent from PostgreSQL: " +
                string.Join(", ", unmappedSource));
        }
    }

    private static async Task<TableMigrationResult> CopyTableAsync(
        SqliteConnection source,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PgTable table,
        PgColumn[] copyColumns,
        CancellationToken cancellationToken)
    {
        var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < copyColumns.Length; index++)
        {
            columnIndex[copyColumns[index].Name] = index;
        }

        var rows = await ReadSourceRowsAsync(source, table.Name, copyColumns, cancellationToken);
        var ordered = OrderRows(table, columnIndex, rows);

        var columnList = string.Join(", ", copyColumns.Select(column => Quote(column.Name)));
        var valueList = string.Join(", ", Enumerable.Range(1, copyColumns.Length).Select(position => $"${position}"));
        var conflictList = string.Join(", ", table.PrimaryKey.Select(Quote));
        var insertSql =
            $"INSERT INTO harness.{Quote(table.Name)} ({columnList}) VALUES ({valueList}) " +
            $"ON CONFLICT ({conflictList}) DO NOTHING RETURNING 1;";

        var inserted = 0;
        foreach (var row in ordered)
        {
            inserted += await InsertOrVerifyIdenticalAsync(
                connection,
                transaction,
                table,
                copyColumns,
                row,
                insertSql,
                cancellationToken);
        }

        return new TableMigrationResult(table.Name, rows.Count, inserted, rows.Count - inserted);
    }

    private static async Task<int> InsertOrVerifyIdenticalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PgTable table,
        PgColumn[] copyColumns,
        object?[] row,
        string insertSql,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = insertSql;
            AddParameters(insert, copyColumns, row);
            if (await insert.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return 1;
            }
        }

        var predicates = string.Join(
            " AND ",
            copyColumns.Select((column, index) => $"{Quote(column.Name)} IS NOT DISTINCT FROM ${index + 1}"));
        await using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText =
            $"SELECT 1 FROM harness.{Quote(table.Name)} WHERE {predicates} LIMIT 1;";
        AddParameters(verify, copyColumns, row);
        if (await verify.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new MigrationDataConflictException(table.Name);
        }

        return 0;
    }

    private static void AddParameters(
        NpgsqlCommand command,
        PgColumn[] copyColumns,
        object?[] row)
    {
        for (var index = 0; index < copyColumns.Length; index++)
        {
            command.Parameters.Add(MigrationValueBinder.ToParameter(copyColumns[index].Kind, row[index]));
        }
    }

    private static async Task<List<object?[]>> ReadSourceRowsAsync(
        SqliteConnection source,
        string tableName,
        PgColumn[] copyColumns,
        CancellationToken cancellationToken)
    {
        var projection = string.Join(", ", copyColumns.Select(column => Quote(column.Name)));
        await using var command = source.CreateCommand();
        command.CommandText = $"SELECT {projection} FROM {Quote(tableName)};";

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[copyColumns.Length];
            for (var index = 0; index < copyColumns.Length; index++)
            {
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(values);
        }

        return rows;
    }

    private static List<object?[]> OrderRows(
        PgTable table,
        IReadOnlyDictionary<string, int> columnIndex,
        List<object?[]> rows)
    {
        if (table.SelfReferences.Count == 0 || rows.Count <= 1)
        {
            return rows;
        }

        // Map each self-reference's referenced-column tuple to the row that owns it, then insert a
        // row only after every parent it points at has been emitted (topological order over rows).
        var identityMaps = table.SelfReferences
            .Select(reference => BuildIdentityMap(reference.ReferencedColumns, columnIndex, rows))
            .ToArray();

        var emitted = new bool[rows.Count];
        var ordered = new List<object?[]>(rows.Count);
        while (ordered.Count < rows.Count)
        {
            var progressed = false;
            for (var index = 0; index < rows.Count; index++)
            {
                if (emitted[index] || !ParentsEmitted(table, columnIndex, identityMaps, rows, index, emitted))
                {
                    continue;
                }

                ordered.Add(rows[index]);
                emitted[index] = true;
                progressed = true;
            }

            if (!progressed)
            {
                throw new MigrationSchemaException(
                    $"Table 'harness.{table.Name}' has a self-referencing cycle in its rows.");
            }
        }

        return ordered;
    }

    private static bool ParentsEmitted(
        PgTable table,
        IReadOnlyDictionary<string, int> columnIndex,
        Dictionary<string, int>[] identityMaps,
        List<object?[]> rows,
        int rowIndex,
        bool[] emitted)
    {
        for (var referenceIndex = 0; referenceIndex < table.SelfReferences.Count; referenceIndex++)
        {
            var reference = table.SelfReferences[referenceIndex];
            var pointer = TupleKey(reference.LocalColumns, columnIndex, rows[rowIndex]);
            if (pointer is null)
            {
                continue;
            }

            if (identityMaps[referenceIndex].TryGetValue(pointer, out var parentIndex)
                && parentIndex != rowIndex
                && !emitted[parentIndex])
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, int> BuildIdentityMap(
        IReadOnlyList<string> referencedColumns,
        IReadOnlyDictionary<string, int> columnIndex,
        List<object?[]> rows)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Count; index++)
        {
            var key = TupleKey(referencedColumns, columnIndex, rows[index]);
            if (key is not null)
            {
                map[key] = index;
            }
        }

        return map;
    }

    private static string? TupleKey(
        IReadOnlyList<string> columns,
        IReadOnlyDictionary<string, int> columnIndex,
        object?[] row)
    {
        var parts = new string[columns.Count];
        for (var index = 0; index < columns.Count; index++)
        {
            if (!columnIndex.TryGetValue(columns[index], out var ordinal) || row[ordinal] is null)
            {
                return null;
            }

            parts[index] = System.Convert.ToString(row[ordinal], System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
        }

        return string.Join('\u001F', parts);
    }

    private static async Task<IReadOnlySet<string>> ReadSqliteColumnsAsync(
        SqliteConnection source,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static SqliteConnection OpenSqlite(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
