namespace Harness.Persistence.Sqlite;

public sealed record SqlitePragmaState(string JournalMode, bool ForeignKeysEnabled, int BusyTimeoutMilliseconds);
