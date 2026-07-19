using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Harness.Persistence.Migration;

/// <summary>
/// Converts a single SQLite cell into a typed PostgreSQL parameter, keyed by the target column's
/// physical kind. SQLite stores every value with type affinity (booleans as 0/1 integers,
/// timestamps and JSON as text), so each kind is bound to the exact PostgreSQL type — preserving
/// <c>version</c> integers verbatim, re-typing timestamps, and letting <c>jsonb</c> normalize
/// canonically (which keeps the audit hash chain re-verifiable).
/// </summary>
internal static class MigrationValueBinder
{
    public static NpgsqlParameter ToParameter(PgColumnKind kind, object? raw)
    {
        var dbType = DbType(kind);
        return raw is null or DBNull
            ? new NpgsqlParameter { NpgsqlDbType = dbType, Value = DBNull.Value }
            : new NpgsqlParameter { NpgsqlDbType = dbType, Value = Convert(kind, raw) };
    }

    private static NpgsqlDbType DbType(PgColumnKind kind) => kind switch
    {
        PgColumnKind.Text => NpgsqlDbType.Text,
        PgColumnKind.Boolean => NpgsqlDbType.Boolean,
        PgColumnKind.Jsonb => NpgsqlDbType.Jsonb,
        PgColumnKind.Json => NpgsqlDbType.Json,
        PgColumnKind.TimestampTz => NpgsqlDbType.TimestampTz,
        PgColumnKind.Timestamp => NpgsqlDbType.Timestamp,
        PgColumnKind.Date => NpgsqlDbType.Date,
        PgColumnKind.BigInt => NpgsqlDbType.Bigint,
        PgColumnKind.Int4 => NpgsqlDbType.Integer,
        PgColumnKind.SmallInt => NpgsqlDbType.Smallint,
        PgColumnKind.Numeric => NpgsqlDbType.Numeric,
        PgColumnKind.Real => NpgsqlDbType.Double,
        PgColumnKind.Bytea => NpgsqlDbType.Bytea,
        _ => throw new MigrationSchemaException($"Unmapped column kind '{kind}'."),
    };

    private static object Convert(PgColumnKind kind, object value) => kind switch
    {
        PgColumnKind.Text => AsString(value),
        PgColumnKind.Jsonb => AsString(value),
        PgColumnKind.Json => AsString(value),
        PgColumnKind.Boolean => System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        PgColumnKind.BigInt => System.Convert.ToInt64(value, CultureInfo.InvariantCulture),
        PgColumnKind.Int4 => System.Convert.ToInt32(value, CultureInfo.InvariantCulture),
        PgColumnKind.SmallInt => System.Convert.ToInt16(value, CultureInfo.InvariantCulture),
        PgColumnKind.Numeric => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        PgColumnKind.Real => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        PgColumnKind.Bytea => AsBytes(value),
        PgColumnKind.TimestampTz => ParseTimestamp(value),
        PgColumnKind.Timestamp => ParseTimestamp(value).UtcDateTime,
        PgColumnKind.Date => DateOnly.FromDateTime(ParseTimestamp(value).UtcDateTime),
        _ => throw new MigrationSchemaException($"Unmapped column kind '{kind}'."),
    };

    private static string AsString(object value) =>
        System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static byte[] AsBytes(object value) => value is byte[] bytes
        ? bytes
        : throw new MigrationSchemaException("Expected a byte array for a bytea column.");

    private static DateTimeOffset ParseTimestamp(object value) => DateTimeOffset.Parse(
        AsString(value),
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);
}
