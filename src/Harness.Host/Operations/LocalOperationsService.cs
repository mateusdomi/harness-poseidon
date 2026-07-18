using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

namespace Harness.Host.Operations;

public sealed class LocalOperationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher;
    private readonly string _databasePath;
    private readonly string _catalogPath;
    private readonly string _backupRoot;

    public LocalOperationsService(
        SqliteWriteDispatcher dispatcher, string databasePath, string catalogPath)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _databasePath = Path.GetFullPath(databasePath);
        _catalogPath = Path.GetFullPath(catalogPath);
        _backupRoot = Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");
    }

    public async Task<BackupHandle> CreateBackupAsync(
        string tenantId, string profileId, string backupId, DateTimeOffset occurredAt,
        CancellationToken token)
    {
        ValidateId(backupId); Directory.CreateDirectory(_backupRoot);
        var finalPath = BackupPath(backupId); var temporaryPath = finalPath + ".tmp";
        if (Directory.Exists(finalPath) || Directory.Exists(temporaryPath))
            throw new InvalidOperationException("The backup identifier already exists.");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            var backupDatabase = Path.Combine(temporaryPath, "harness.db");
            await _dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
            {
                await using var destination = Open(backupDatabase);
                await destination.OpenAsync(cancellationToken); connection.BackupDatabase(destination);
            }, token);
            CopyDirectory(_catalogPath, Path.Combine(temporaryPath, "catalog"));
            await File.WriteAllTextAsync(Path.Combine(temporaryPath, "manifest.json"),
                JsonSerializer.Serialize(new { backupId, createdAt = occurredAt }, JsonOptions), token);
            Directory.Move(temporaryPath, finalPath);
            await AppendAuditAsync(tenantId, profileId, "backup.created", backupId,
                "Local backup created.", occurredAt, token);
            return new(backupId, occurredAt);
        }
        catch
        {
            DeleteOwnedDirectory(temporaryPath); DeleteOwnedDirectory(finalPath); throw;
        }
    }

    public async Task RestoreBackupAsync(
        string tenantId, string profileId, string backupId, DateTimeOffset occurredAt,
        CancellationToken token)
    {
        ValidateId(backupId); var sourceRoot = BackupPath(backupId);
        var sourceDatabase = Path.Combine(sourceRoot, "harness.db");
        if (!File.Exists(sourceDatabase)) throw new LocalBackupNotFoundException();
        var operationId = UlidValue.New(occurredAt).ToString();
        var rollbackDatabase = Path.Combine(_backupRoot, $"restore-{operationId}.db");
        var stagedCatalog = Path.Combine(_backupRoot, $"restore-{operationId}-catalog");
        var rollbackCatalog = Path.Combine(_backupRoot, $"restore-{operationId}-rollback-catalog");
        try
        {
            CopyDirectory(Path.Combine(sourceRoot, "catalog"), stagedCatalog);
            CopyDirectory(_catalogPath, rollbackCatalog);
            await _dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
            {
                await using (var rollback = Open(rollbackDatabase))
                { await rollback.OpenAsync(cancellationToken); connection.BackupDatabase(rollback); }
                await using var source = Open(sourceDatabase, true);
                await source.OpenAsync(cancellationToken); source.BackupDatabase(connection);
            }, token);
            try { ReplaceCatalog(stagedCatalog, rollbackCatalog); }
            catch
            {
                await RestoreDatabaseAsync(rollbackDatabase, token); throw;
            }
            await AppendAuditAsync(tenantId, profileId, "backup.restored", backupId,
                "Local backup restored.", occurredAt, token);
        }
        finally
        {
            if (File.Exists(rollbackDatabase)) File.Delete(rollbackDatabase);
            DeleteOwnedDirectory(stagedCatalog); DeleteOwnedDirectory(rollbackCatalog);
        }
    }

    public async Task<IReadOnlyList<DiagnosticCheck>> DiagnoseAsync(CancellationToken token)
    {
        var checks = new List<DiagnosticCheck>();
        try
        {
            var integrity = await _dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
            { await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA quick_check;"; return Convert.ToString(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); }, token);
            checks.Add(new("database", integrity == "ok" ? "ok" : "error",
                integrity == "ok" ? "SQLite quick_check passed." : $"SQLite quick_check: {integrity}."));
        }
        catch (SqliteException exception) { checks.Add(new("database", "error", $"SQLite error {exception.SqliteErrorCode}.")); }
        checks.Add(new("catalog", Directory.Exists(_catalogPath) ? "ok" : "warning",
            Directory.Exists(_catalogPath) ? "Document catalog is available." : "Document catalog has not been created yet."));
        checks.Add(new("backups", Directory.Exists(_backupRoot) ? "ok" : "warning",
            Directory.Exists(_backupRoot) ? "Local backup directory is available." : "No local backup has been created yet."));
        checks.Add(new("realtime", "ok", "Persisted realtime endpoint and SignalR hub are enabled."));
        return checks;
    }

    private Task RestoreDatabaseAsync(string sourcePath, CancellationToken token) =>
        _dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
        { await using var source = Open(sourcePath, true); await source.OpenAsync(cancellationToken); source.BackupDatabase(connection); }, token);

    private Task AppendAuditAsync(string tenantId, string profileId, string action,
        string backupId, string detail, DateTimeOffset at, CancellationToken token) =>
        _dispatcher.ExecuteAsync(async (connection, cancellationToken) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var auditId = UlidValue.New(at).ToString(); var payload = JsonSerializer.Serialize(new
            { auditEvent = new { id = auditId, actorKind = "user", actorId = profileId, action, targetType = "backup", targetId = backupId, detail, occurredAt = at } }, JsonOptions);
            long sequence; string previous; await using (var latest = connection.CreateCommand())
            {
                latest.Transaction = transaction; latest.CommandText = "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
                latest.Parameters.AddWithValue("$tenant", tenantId); await using var reader = await latest.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) { sequence = reader.GetInt64(0) + 1; previous = reader.GetString(1); }
                else { sequence = 1; previous = AuditLedgerHash.Genesis; }
            }
            var hash = AuditLedgerHash.Compute(previous, tenantId, sequence, action, payload, at);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO audit_ledger(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) VALUES($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);", cancellationToken,
                ("$id", UlidValue.New(at).ToString()), ("$tenant", tenantId), ("$sequence", sequence), ("$previous", previous), ("$hash", hash), ("$type", action), ("$payload", payload), ("$at", Store(at)));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO outbox_messages(id,tenant_id,event_type,payload_json,occurred_at) VALUES($id,$tenant,'audit.eventAppended',$payload,$at);", cancellationToken,
                ("$id", UlidValue.New(at).ToString()), ("$tenant", tenantId), ("$payload", payload), ("$at", Store(at)));
            await transaction.CommitAsync(cancellationToken);
        }, token);

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) { Directory.CreateDirectory(destination); return; }
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            var current = pending.Pop(); Directory.CreateDirectory(current.Destination);
            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue;
                pending.Push((directory, Path.Combine(current.Destination, Path.GetFileName(directory))));
            }
            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), false);
            }
        }
    }

    private void ReplaceCatalog(string staged, string rollback)
    {
        try
        {
            if (Directory.Exists(_catalogPath)) Directory.Delete(_catalogPath, true);
            Directory.Move(staged, _catalogPath);
        }
        catch
        {
            if (Directory.Exists(_catalogPath)) Directory.Delete(_catalogPath, true);
            if (Directory.Exists(rollback)) Directory.Move(rollback, _catalogPath);
            throw;
        }
    }

    private string BackupPath(string id)
    {
        var value = Path.GetFullPath(Path.Combine(_backupRoot, id));
        if (!value.StartsWith(Path.GetFullPath(_backupRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Backup path is invalid.", nameof(id));
        return value;
    }
    private static SqliteConnection Open(string path, bool readOnly = false) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    private static void ValidateId(string value) { if (!UlidValue.TryParse(value, out _)) throw new ArgumentException("Backup ID must be a ULID.", nameof(value)); }
    private void DeleteOwnedDirectory(string path) { if (path.StartsWith(Path.GetFullPath(_backupRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(path)) Directory.Delete(path, true); }
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token, params (string Name, object Value)[] values)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); await command.ExecuteNonQueryAsync(token); }
    private static string Store(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public sealed class LocalBackupNotFoundException : Exception;
