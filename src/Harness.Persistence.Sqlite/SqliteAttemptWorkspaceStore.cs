using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Microsoft.Data.Sqlite;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Sqlite;

public sealed class SqliteAttemptWorkspaceStore(SqliteWriteDispatcher dispatcher) : IAttemptWorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteWriteDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task<AttemptWorkspaceReceipt> AcquireAsync(
        AttemptWorkspaceAcquireCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = AttemptWorkspaceAcquireValidator.Normalize(command);
        var commandHash = AttemptWorkspaceCommandHash.Compute(normalized);
        return _dispatcher.ExecuteAsync(
            (connection, token) => AcquireCoreAsync(connection, normalized, commandHash, token),
            cancellationToken);
    }

    public Task<AttemptWorkspaceSnapshot?> GetAsync(
        string tenantId,
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceAcquireValidator.ValidateUlid(tenantId, nameof(tenantId));
        AttemptWorkspaceAcquireValidator.ValidateUlid(attemptId, nameof(attemptId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReadAsync(connection, null, tenantId, attemptId, token),
            cancellationToken);
    }

    public Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListExpiredAsync(
        string tenantId,
        DateTimeOffset expiredBefore,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceAcquireValidator.ValidateUlid(tenantId, nameof(tenantId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ListExpiredCoreAsync(connection, tenantId, expiredBefore, token),
            cancellationToken);
    }

    public Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListActiveAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceAcquireValidator.ValidateUlid(tenantId, nameof(tenantId));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ListActiveCoreAsync(connection, tenantId, token),
            cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> HeartbeatAsync(
        AttemptWorkspaceLeaseCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.TenantId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.AttemptId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateOwner(command.Owner, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateLeaseDuration(command.LeaseDuration, nameof(command));
        return _dispatcher.ExecuteAsync(
            (connection, token) => HeartbeatCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> ReclaimExpiredAsync(
        AttemptWorkspaceReclaimCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.TenantId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.AttemptId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateOwner(command.NewOwner, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateLeaseDuration(command.LeaseDuration, nameof(command));
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReclaimCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> TransitionAsync(
        AttemptWorkspaceTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceTransitionValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => TransitionCoreAsync(connection, command, token),
            cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> ReleaseAsync(
        AttemptWorkspaceReleaseCommand command,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceReleaseValidator.Validate(command);
        return _dispatcher.ExecuteAsync(
            (connection, token) => ReleaseCoreAsync(connection, command, token),
            cancellationToken);
    }

    private static async Task<AttemptWorkspaceReceipt> AcquireCoreAsync(
        SqliteConnection connection,
        AttemptWorkspaceAcquireCommand command,
        string commandHash,
        CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var replayed = await ReadInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            commandHash,
            token);
        if (replayed)
        {
            var replaySnapshot = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token)
                ?? throw new InvalidOperationException("An acquire inbox receipt exists without its workspace.");
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, replaySnapshot, []);
        }

        var existing = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token);
        if (existing is not null)
        {
            await transaction.CommitAsync(token);
            return MatchesAcquire(existing, command)
                ? new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, existing, [])
                : new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, existing, []);
        }

        var conflicts = await FindScopeConflictsAsync(connection, transaction, command, token);
        if (conflicts.Count != 0)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.ScopeConflict, null, conflicts);
        }

        var storedAt = Store(command.OccurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO attempt_workspaces
                (tenant_id, project_id, task_id, attempt_id, repository_root, controlled_root,
                 base_reference, branch_name, worktree_path, state, cleanup_state, owner,
                 fencing_token, lease_expires_at, last_heartbeat_at, created_at, updated_at)
            VALUES ($tenant, $project, $task, $attempt, $repository, $controlledRoot,
                    $base, $branch, $worktree, 'claimed', 'not_required', $owner,
                    1, $leaseExpiresAt, $at, $at, $at);
            """,
            token,
            ("$tenant", command.TenantId),
            ("$project", command.ProjectId),
            ("$task", command.TaskId),
            ("$attempt", command.AttemptId),
            ("$repository", command.RepositoryRoot),
            ("$controlledRoot", command.ControlledRoot),
            ("$base", command.BaseReference),
            ("$branch", command.BranchName),
            ("$worktree", command.WorktreePath),
            ("$owner", command.Owner),
            ("$leaseExpiresAt", Store(command.OccurredAt + command.LeaseDuration)),
            ("$at", storedAt));
        foreach (var claim in command.ScopeClaims)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO attempt_scope_claims (id, tenant_id, project_id, attempt_id, path_pattern) " +
                "VALUES ($id, $tenant, $project, $attempt, $claim);",
                token,
                ("$id", UlidValue.New(command.OccurredAt).ToString()),
                ("$tenant", command.TenantId),
                ("$project", command.ProjectId),
                ("$attempt", command.AttemptId),
                ("$claim", claim));
        }

        var snapshot = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token)
            ?? throw new InvalidOperationException("The acquired workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceClaimed",
            SerializePayload(snapshot),
            command.OccurredAt,
            token);
        await WriteInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            commandHash,
            command.AttemptId,
            command.OccurredAt,
            token);
        await transaction.CommitAsync(token);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<AttemptWorkspaceReceipt> HeartbeatCoreAsync(
        SqliteConnection connection,
        AttemptWorkspaceLeaseCommand command,
        CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token);
        if (current is null)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE attempt_workspaces SET lease_expires_at=$leaseExpiresAt,last_heartbeat_at=$at," +
            "updated_at=$at,version=version+1 WHERE tenant_id=$tenant AND attempt_id=$attempt;",
            token,
            ("$leaseExpiresAt", Store(command.OccurredAt + command.LeaseDuration)),
            ("$at", Store(command.OccurredAt)),
            ("$tenant", command.TenantId),
            ("$attempt", command.AttemptId));
        await transaction.CommitAsync(token);
        var snapshot = await ReadAsync(connection, null, command.TenantId, command.AttemptId, token)
            ?? throw new InvalidOperationException("The heartbeated workspace was not persisted.");
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<AttemptWorkspaceReceipt> ReclaimCoreAsync(
        SqliteConnection connection,
        AttemptWorkspaceReclaimCommand command,
        CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token);
        if (current is null)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (current.LeaseExpiresAt > command.OccurredAt)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE attempt_workspaces SET owner=$owner,fencing_token=fencing_token+1," +
            "lease_expires_at=$leaseExpiresAt,last_heartbeat_at=$at,updated_at=$at,version=version+1 " +
            "WHERE tenant_id=$tenant AND attempt_id=$attempt;",
            token,
            ("$owner", command.NewOwner),
            ("$leaseExpiresAt", Store(command.OccurredAt + command.LeaseDuration)),
            ("$at", Store(command.OccurredAt)),
            ("$tenant", command.TenantId),
            ("$attempt", command.AttemptId));
        var snapshot = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token)
            ?? throw new InvalidOperationException("The reclaimed workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceReclaimed",
            SerializePayload(snapshot),
            command.OccurredAt,
            token);
        await transaction.CommitAsync(token);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<AttemptWorkspaceReceipt> TransitionCoreAsync(
        SqliteConnection connection,
        AttemptWorkspaceTransitionCommand command,
        CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token);
        if (current is null)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        if (current.State == command.State &&
            (command.CommitSha is null || current.CommitSha == command.CommitSha) &&
            (command.SessionId is null || current.SessionId == command.SessionId) &&
            (command.TechnicalExecutionId is null ||
                current.TechnicalExecutionId == command.TechnicalExecutionId))
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, current, []);
        }

        if (current.State != command.ExpectedState)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE attempt_workspaces SET state=$state,cleanup_state=$cleanupState," +
            "commit_sha=COALESCE($commit,commit_sha),session_id=COALESCE($session,session_id)," +
            "technical_execution_id=COALESCE($execution,technical_execution_id)," +
            "final_error=$finalError,updated_at=$at,version=version+1 " +
            "WHERE tenant_id=$tenant AND attempt_id=$attempt AND state=$expected;",
            token,
            ("$state", AttemptWorkspaceStateCodec.ToStorage(command.State)),
            ("$cleanupState", AttemptWorkspaceCleanupStateCodec.ToStorage(
                AttemptWorkspaceLifecycle.CleanupStateFor(command.State))),
            ("$commit", NullableText(command.CommitSha)),
            ("$session", NullableText(command.SessionId)),
            ("$execution", NullableText(command.TechnicalExecutionId)),
            ("$finalError", NullableText(command.FinalError)),
            ("$at", Store(command.OccurredAt)),
            ("$tenant", command.TenantId),
            ("$attempt", command.AttemptId),
            ("$expected", AttemptWorkspaceStateCodec.ToStorage(command.ExpectedState)));
        var snapshot = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token)
            ?? throw new InvalidOperationException("The transitioned workspace was not persisted.");
        var payload = SerializePayload(snapshot);
        var storedState = AttemptWorkspaceStateCodec.ToStorage(command.State);
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            $"attempt.workspace{char.ToUpperInvariant(storedState[0])}{storedState[1..]}",
            payload,
            command.OccurredAt,
            token);
        var eventType = command.State switch
        {
            AttemptWorkspaceState.Running => "attempt.started",
            AttemptWorkspaceState.Completed => "attempt.completed",
            AttemptWorkspaceState.Failed => "attempt.failed",
            _ => null,
        };
        if (eventType is not null)
        {
            await AppendOutboxAsync(
                connection,
                transaction,
                command.TenantId,
                eventType,
                payload,
                command.OccurredAt,
                token);
        }

        await transaction.CommitAsync(token);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<AttemptWorkspaceReceipt> ReleaseCoreAsync(
        SqliteConnection connection,
        AttemptWorkspaceReleaseCommand command,
        CancellationToken token)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        var current = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token);
        if (current is null)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, current, []);
        }

        if (!AttemptWorkspaceLifecycle.IsTerminal(current.State))
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(token);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        var storedAt = Store(command.OccurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE attempt_scope_claims SET released_at=$at " +
            "WHERE tenant_id=$tenant AND attempt_id=$attempt AND released_at IS NULL;",
            token,
            ("$at", storedAt),
            ("$tenant", command.TenantId),
            ("$attempt", command.AttemptId));
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE attempt_workspaces SET cleanup_state='completed',released_at=$at,updated_at=$at," +
            "version=version+1 WHERE tenant_id=$tenant AND attempt_id=$attempt;",
            token,
            ("$at", storedAt),
            ("$tenant", command.TenantId),
            ("$attempt", command.AttemptId));
        var snapshot = await ReadAsync(connection, transaction, command.TenantId, command.AttemptId, token)
            ?? throw new InvalidOperationException("The released workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceCleaned",
            SerializePayload(snapshot),
            command.OccurredAt,
            token);
        await transaction.CommitAsync(token);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<IReadOnlyList<AttemptScopeConflict>> FindScopeConflictsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AttemptWorkspaceAcquireCommand command,
        CancellationToken token)
    {
        var activeClaims = new List<(string AttemptId, string Claim)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT attempt_id,path_pattern FROM attempt_scope_claims " +
                "WHERE tenant_id=$tenant AND project_id=$project AND released_at IS NULL " +
                "ORDER BY attempt_id,path_pattern;";
            Add(query, "$tenant", command.TenantId);
            Add(query, "$project", command.ProjectId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                activeClaims.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        return command.ScopeClaims
            .SelectMany(requested => activeClaims
                .Where(existing => AttemptWorkspaceScopePattern.Intersects(requested, existing.Claim))
                .Select(existing => new AttemptScopeConflict(existing.AttemptId, requested, existing.Claim)))
            .ToArray();
    }

    private static async Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListExpiredCoreAsync(
        SqliteConnection connection,
        string tenantId,
        DateTimeOffset expiredBefore,
        CancellationToken token)
    {
        var attemptIds = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = null;
            query.CommandText =
                "SELECT attempt_id FROM attempt_workspaces " +
                "WHERE tenant_id=$tenant AND released_at IS NULL AND lease_expires_at<=$before " +
                "ORDER BY lease_expires_at,attempt_id;";
            Add(query, "$tenant", tenantId);
            Add(query, "$before", Store(expiredBefore));
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                attemptIds.Add(reader.GetString(0));
            }
        }

        var snapshots = new List<AttemptWorkspaceSnapshot>(attemptIds.Count);
        foreach (var attemptId in attemptIds)
        {
            snapshots.Add(await ReadAsync(connection, null, tenantId, attemptId, token)
                ?? throw new InvalidOperationException("An expired workspace disappeared while listing."));
        }

        return snapshots;
    }

    private static async Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListActiveCoreAsync(
        SqliteConnection connection,
        string tenantId,
        CancellationToken token)
    {
        var attemptIds = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT attempt_id FROM attempt_workspaces " +
                "WHERE tenant_id=$tenant AND released_at IS NULL " +
                "ORDER BY created_at,attempt_id;";
            Add(query, "$tenant", tenantId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                attemptIds.Add(reader.GetString(0));
            }
        }

        var snapshots = new List<AttemptWorkspaceSnapshot>(attemptIds.Count);
        foreach (var attemptId in attemptIds)
        {
            snapshots.Add(await ReadAsync(connection, null, tenantId, attemptId, token)
                ?? throw new InvalidOperationException("An active workspace disappeared while listing."));
        }

        return snapshots;
    }

    private static async Task<AttemptWorkspaceSnapshot?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tenantId,
        string attemptId,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT tenant_id,project_id,task_id,attempt_id,technical_execution_id,repository_root," +
            "controlled_root,base_reference,branch_name,worktree_path,state,cleanup_state,owner," +
            "fencing_token,lease_expires_at,last_heartbeat_at,commit_sha,session_id,final_error," +
            "created_at,updated_at,released_at,version " +
            "FROM attempt_workspaces WHERE tenant_id=$tenant AND attempt_id=$attempt;";
        Add(query, "$tenant", tenantId);
        Add(query, "$attempt", attemptId);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return null;
        }

        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        await reader.DisposeAsync();
        var claims = new List<AttemptScopeClaimSnapshot>();
        await using var claimQuery = connection.CreateCommand();
        claimQuery.Transaction = transaction;
        claimQuery.CommandText =
            "SELECT id,path_pattern,released_at FROM attempt_scope_claims " +
            "WHERE tenant_id=$tenant AND attempt_id=$attempt ORDER BY path_pattern;";
        Add(claimQuery, "$tenant", tenantId);
        Add(claimQuery, "$attempt", attemptId);
        await using var claimReader = await claimQuery.ExecuteReaderAsync(token);
        while (await claimReader.ReadAsync(token))
        {
            claims.Add(new AttemptScopeClaimSnapshot(
                claimReader.GetString(0),
                claimReader.GetString(1),
                claimReader.IsDBNull(2) ? null : Load(claimReader.GetString(2))));
        }

        return new AttemptWorkspaceSnapshot
        {
            TenantId = (string)values[0],
            ProjectId = (string)values[1],
            TaskId = (string)values[2],
            AttemptId = (string)values[3],
            TechnicalExecutionId = values[4] is DBNull ? null : (string)values[4],
            RepositoryRoot = (string)values[5],
            ControlledRoot = (string)values[6],
            BaseReference = (string)values[7],
            BranchName = (string)values[8],
            WorktreePath = (string)values[9],
            State = AttemptWorkspaceStateCodec.Parse((string)values[10]),
            CleanupState = AttemptWorkspaceCleanupStateCodec.Parse((string)values[11]),
            ScopeClaims = claims,
            Owner = (string)values[12],
            FencingToken = Convert.ToInt64(values[13], CultureInfo.InvariantCulture),
            LeaseExpiresAt = Load((string)values[14]),
            LastHeartbeatAt = Load((string)values[15]),
            CommitSha = values[16] is DBNull ? null : (string)values[16],
            SessionId = values[17] is DBNull ? null : (string)values[17],
            FinalError = values[18] is DBNull ? null : (string)values[18],
            CreatedAt = Load((string)values[19]),
            UpdatedAt = Load((string)values[20]),
            ReleasedAt = values[21] is DBNull ? null : Load((string)values[21]),
            Version = Convert.ToInt64(values[22], CultureInfo.InvariantCulture),
        };
    }

    private static bool OwnsLease(AttemptWorkspaceSnapshot current, string owner, long fencingToken) =>
        string.Equals(current.Owner, owner, StringComparison.Ordinal) &&
        current.FencingToken == fencingToken;

    private static bool MatchesAcquire(
        AttemptWorkspaceSnapshot existing,
        AttemptWorkspaceAcquireCommand command) =>
        existing.ProjectId == command.ProjectId &&
        existing.TaskId == command.TaskId &&
        existing.RepositoryRoot == command.RepositoryRoot &&
        existing.ControlledRoot == command.ControlledRoot &&
        existing.BaseReference == command.BaseReference &&
        existing.BranchName == command.BranchName &&
        existing.WorktreePath == command.WorktreePath &&
        existing.ScopeClaims.Select(claim => claim.PathPattern)
            .SequenceEqual(command.ScopeClaims, StringComparer.OrdinalIgnoreCase);

    private static string SerializePayload(AttemptWorkspaceSnapshot snapshot) =>
        JsonSerializer.Serialize(
            new AttemptWorkspaceLifecycleEventPayload(
                snapshot.ProjectId,
                snapshot.TaskId,
                snapshot.AttemptId,
                AttemptWorkspaceStateCodec.ToStorage(snapshot.State),
                AttemptWorkspaceCleanupStateCodec.ToStorage(snapshot.CleanupState),
                snapshot.BranchName,
                snapshot.WorktreePath,
                snapshot.ScopeClaims.Select(claim => claim.PathPattern).ToArray(),
                snapshot.Owner,
                snapshot.FencingToken,
                snapshot.TechnicalExecutionId,
                snapshot.CommitSha,
                snapshot.SessionId,
                snapshot.FinalError),
            JsonOptions);

    private static async Task<bool> ReadInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        CancellationToken token)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash FROM inbox_messages WHERE tenant_id=$tenant AND idempotency_key=$key;";
        Add(query, "$tenant", tenantId);
        Add(query, "$key", idempotencyKey);
        await using var reader = await query.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return false;
        }

        if (!string.Equals(reader.GetString(0), commandHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different attempt workspace command.");
        }

        return true;
    }

    private static Task WriteInboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        string attemptId,
        DateTimeOffset occurredAt,
        CancellationToken token) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO inbox_messages (tenant_id,idempotency_key,message_hash,response_json,processed_at) " +
            "VALUES ($tenant,$key,$hash,$response,$at);",
            token,
            ("$tenant", tenantId),
            ("$key", idempotencyKey),
            ("$hash", commandHash),
            ("$response", JsonSerializer.Serialize(new AttemptWorkspaceInboxReceipt(attemptId), JsonOptions)),
            ("$at", Store(occurredAt)));

    private static async Task AppendAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken token)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            "SELECT sequence,event_hash FROM audit_ledger WHERE tenant_id=$tenant ORDER BY sequence DESC LIMIT 1;";
        Add(tail, "$tenant", tenantId);
        await using var reader = await tail.ExecuteReaderAsync(token);
        var exists = await reader.ReadAsync(token);
        var sequence = exists ? reader.GetInt64(0) + 1 : 1;
        var previous = exists ? reader.GetString(1) : AuditLedgerHash.Genesis;
        await reader.DisposeAsync();
        var hash = AuditLedgerHash.Compute(previous, tenantId, sequence, eventType, payload, occurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO audit_ledger " +
            "(id,tenant_id,sequence,previous_hash,event_hash,event_type,payload_json,occurred_at) " +
            "VALUES ($id,$tenant,$sequence,$previous,$hash,$type,$payload,$at);",
            token,
            ("$id", UlidValue.New(occurredAt).ToString()),
            ("$tenant", tenantId),
            ("$sequence", sequence),
            ("$previous", previous),
            ("$hash", hash),
            ("$type", eventType),
            ("$payload", payload),
            ("$at", Store(occurredAt)));
    }

    private static Task AppendOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken token) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO outbox_messages (id,tenant_id,event_type,payload_json,occurred_at) " +
            "VALUES ($id,$tenant,$type,$payload,$at);",
            token,
            ("$id", UlidValue.New(occurredAt).ToString()),
            ("$tenant", tenantId),
            ("$type", eventType),
            ("$payload", PersistenceSanitizer.SanitizeJson(payload)),
            ("$at", Store(occurredAt)));

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken token,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(token);
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static object NullableText(string? value) =>
        value ?? (object)DBNull.Value;

    private static string Store(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Load(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record AttemptWorkspaceInboxReceipt(string AttemptId);
}
