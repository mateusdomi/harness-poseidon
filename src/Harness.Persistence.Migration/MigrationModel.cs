namespace Harness.Persistence.Migration;

/// <summary>
/// Closed set of PostgreSQL column kinds the migrator knows how to bind. The migrator fails
/// closed (throws <see cref="MigrationSchemaException"/>) for any physical type outside this set,
/// so an unmapped column can never be silently dropped or corrupted.
/// </summary>
public enum PgColumnKind
{
    Text,
    Boolean,
    Jsonb,
    Json,
    TimestampTz,
    Timestamp,
    Date,
    BigInt,
    Int4,
    SmallInt,
    Numeric,
    Real,
    Bytea,
}

/// <summary>A single PostgreSQL column, in physical (attnum) order.</summary>
public sealed record PgColumn(string Name, PgColumnKind Kind, bool IsNotNull, bool HasDefault);

/// <summary>
/// A self-referencing foreign key: the local columns (<paramref name="LocalColumns"/>) of a row
/// point at the referenced columns (<paramref name="ReferencedColumns"/>) of another row in the
/// same table. Used to order rows so a parent is always inserted before the child that supersedes
/// it (for example <c>document_versions.supersedes_id</c>).
/// </summary>
public sealed record PgSelfReference(
    IReadOnlyList<string> LocalColumns,
    IReadOnlyList<string> ReferencedColumns);

/// <summary>A base table in the <c>harness</c> schema, fully introspected from <c>pg_catalog</c>.</summary>
public sealed record PgTable(
    string Name,
    IReadOnlyList<PgColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<string> ForeignTables,
    IReadOnlyList<PgSelfReference> SelfReferences);

/// <summary>Per-table outcome of a migration run.</summary>
public sealed record TableMigrationResult(
    string Table,
    int SourceRows,
    int Inserted,
    int SkippedExisting);

/// <summary>A table present in PostgreSQL but not copied, with the reason it was deferred.</summary>
public sealed record DeferredTable(string Table, string Reason);

/// <summary>
/// Typed, fully deterministic report of a migration run. Every PostgreSQL base table is accounted
/// for either in <see cref="Tables"/> (copied) or <see cref="Deferred"/> (skipped, with reason);
/// there is no silent truncation.
/// </summary>
public sealed record MigrationReport(
    IReadOnlyList<TableMigrationResult> Tables,
    IReadOnlyList<DeferredTable> Deferred)
{
    public int TablesCovered => Tables.Count;

    public int TotalSourceRows => Tables.Sum(table => table.SourceRows);

    public int TotalInserted => Tables.Sum(table => table.Inserted);

    public int TotalSkippedExisting => Tables.Sum(table => table.SkippedExisting);
}
