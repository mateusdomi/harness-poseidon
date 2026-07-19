namespace Harness.Persistence.Migration;

/// <summary>
/// Raised when the SQLite source and the PostgreSQL target cannot be reconciled safely: a required
/// PostgreSQL column has no SQLite counterpart, a physical type is unmapped, or the foreign-key
/// graph contains a cycle. The migrator fails closed rather than migrate partial or corrupt data.
/// </summary>
public sealed class MigrationSchemaException : Exception
{
    public MigrationSchemaException(string message)
        : base(message)
    {
    }

    public MigrationSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
