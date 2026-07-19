namespace Harness.Persistence.Migration;

/// <summary>
/// Raised when a target row has the same primary key as a SQLite source row but different data.
/// The exception intentionally omits row values so migration diagnostics cannot disclose domain
/// content or secrets.
/// </summary>
public sealed class MigrationDataConflictException : Exception
{
    public MigrationDataConflictException(string tableName)
        : base($"Table 'harness.{tableName}' contains a conflicting target row.")
    {
        TableName = tableName;
    }

    public string TableName { get; }
}
