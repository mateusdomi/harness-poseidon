using Npgsql;

namespace Harness.Persistence.Migration;

/// <summary>
/// Introspects the live <c>harness</c> schema from <c>pg_catalog</c> so the migrator is driven by
/// the actual target schema (never a hand-maintained table list that could drift). Returns the
/// domain tables topologically ordered by foreign-key dependencies, with per-table columns,
/// primary keys, and self-references.
/// </summary>
internal static class PostgresSchemaReader
{
    private const string Schema = "harness";

    public static async Task<IReadOnlyList<PgTable>> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(connection, transaction, cancellationToken);
        var primaryKeys = await ReadPrimaryKeysAsync(connection, transaction, cancellationToken);
        var (foreignTables, selfReferences) =
            await ReadForeignKeysAsync(connection, transaction, columns, cancellationToken);

        var tables = new List<PgTable>();
        foreach (var (name, tableColumns) in columns)
        {
            if (!primaryKeys.TryGetValue(name, out var pk) || pk.Count == 0)
            {
                throw new MigrationSchemaException(
                    $"Table 'harness.{name}' has no primary key; the migrator cannot guarantee idempotency.");
            }

            tables.Add(new PgTable(
                name,
                tableColumns,
                pk,
                foreignTables.TryGetValue(name, out var refs) ? refs : [],
                selfReferences.TryGetValue(name, out var self) ? self : []));
        }

        return TopologicalSort(tables);
    }

    private static async Task<IReadOnlyList<(string Table, IReadOnlyList<PgColumn> Columns)>> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT c.relname, a.attname, t.typname, a.attnotnull, a.atthasdef
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            WHERE n.nspname = 'harness'
              AND c.relkind = 'r'
              AND c.relname <> 'schema_migrations'
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY c.relname, a.attnum;
            """;

        var ordered = new List<(string Table, IReadOnlyList<PgColumn> Columns)>();
        var current = new List<PgColumn>();
        var currentTable = string.Empty;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!string.Equals(table, currentTable, StringComparison.Ordinal))
            {
                if (currentTable.Length > 0)
                {
                    ordered.Add((currentTable, current));
                }

                currentTable = table;
                current = [];
            }

            current.Add(new PgColumn(
                reader.GetString(1),
                MapKind(table, reader.GetString(1), reader.GetString(2)),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }

        if (currentTable.Length > 0)
        {
            ordered.Add((currentTable, current));
        }

        return ordered;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadPrimaryKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT rel.relname, a.attname, k.ord
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = rel.relnamespace
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON TRUE
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            WHERE con.contype = 'p' AND n.nspname = 'harness'
            ORDER BY rel.relname, k.ord;
            """;

        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            if (!result.TryGetValue(table, out var columns))
            {
                columns = [];
                result[table] = columns;
            }

            columns.Add(reader.GetString(1));
        }

        return result.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal);
    }

    private static async Task<(
        IReadOnlyDictionary<string, IReadOnlyList<string>> ForeignTables,
        IReadOnlyDictionary<string, IReadOnlyList<PgSelfReference>> SelfReferences)> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<(string Table, IReadOnlyList<PgColumn> Columns)> columns,
        CancellationToken cancellationToken)
    {
        var attributeNames = await ReadAttributeNamesAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT con.oid, rel.relname, fref.relname, con.conkey, con.confkey,
                   con.conrelid, con.confrelid
            FROM pg_constraint con
            JOIN pg_class rel ON rel.oid = con.conrelid
            JOIN pg_class fref ON fref.oid = con.confrelid
            JOIN pg_namespace n ON n.oid = rel.relnamespace
            WHERE con.contype = 'f' AND n.nspname = 'harness'
            ORDER BY con.oid;
            """;

        var foreignTables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var selfReferences = new Dictionary<string, List<PgSelfReference>>(StringComparer.Ordinal);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(1);
            var referencedTable = reader.GetString(2);
            var localKeys = reader.GetFieldValue<short[]>(3);
            var foreignKeys = reader.GetFieldValue<short[]>(4);
            var localRelId = reader.GetFieldValue<uint>(5);
            var foreignRelId = reader.GetFieldValue<uint>(6);

            if (string.Equals(table, referencedTable, StringComparison.Ordinal))
            {
                var localColumns = localKeys
                    .Select(attnum => attributeNames[(localRelId, attnum)])
                    .ToArray();
                var referencedColumns = foreignKeys
                    .Select(attnum => attributeNames[(foreignRelId, attnum)])
                    .ToArray();
                if (!selfReferences.TryGetValue(table, out var list))
                {
                    list = [];
                    selfReferences[table] = list;
                }

                list.Add(new PgSelfReference(localColumns, referencedColumns));
            }
            else
            {
                if (!foreignTables.TryGetValue(table, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    foreignTables[table] = set;
                }

                set.Add(referencedTable);
            }
        }

        _ = columns;
        return (
            foreignTables.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            selfReferences.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<PgSelfReference>)entry.Value,
                StringComparer.Ordinal));
    }

    private static async Task<IReadOnlyDictionary<(uint RelId, short AttNum), string>> ReadAttributeNamesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT a.attrelid, a.attnum, a.attname
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'harness' AND a.attnum > 0 AND NOT a.attisdropped;
            """;

        var result = new Dictionary<(uint, short), string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[(reader.GetFieldValue<uint>(0), reader.GetInt16(1))] = reader.GetString(2);
        }

        return result;
    }

    private static List<PgTable> TopologicalSort(List<PgTable> tables)
    {
        var byName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
        var ordered = new List<PgTable>(tables.Count);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var remaining = tables.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(name => byName[name].ForeignTables.All(
                    dependency => !byName.ContainsKey(dependency) || done.Contains(dependency)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (ready.Length == 0)
            {
                throw new MigrationSchemaException(
                    "The 'harness' foreign-key graph contains a cycle across tables: " +
                    string.Join(", ", remaining.OrderBy(name => name, StringComparer.Ordinal)));
            }

            foreach (var name in ready)
            {
                ordered.Add(byName[name]);
                done.Add(name);
                remaining.Remove(name);
            }
        }

        return ordered;
    }

    private static PgColumnKind MapKind(string table, string column, string typeName) => typeName switch
    {
        "bool" => PgColumnKind.Boolean,
        "jsonb" => PgColumnKind.Jsonb,
        "json" => PgColumnKind.Json,
        "timestamptz" => PgColumnKind.TimestampTz,
        "timestamp" => PgColumnKind.Timestamp,
        "date" => PgColumnKind.Date,
        "int8" => PgColumnKind.BigInt,
        "int4" => PgColumnKind.Int4,
        "int2" => PgColumnKind.SmallInt,
        "numeric" => PgColumnKind.Numeric,
        "float8" => PgColumnKind.Real,
        "float4" => PgColumnKind.Real,
        "bytea" => PgColumnKind.Bytea,
        "bpchar" or "varchar" or "text" or "uuid" or "name" => PgColumnKind.Text,
        _ => throw new MigrationSchemaException(
            $"Column 'harness.{table}.{column}' has unmapped physical type '{typeName}'."),
    };
}
