using System.Text.Json;
using Harness.Persistence.Abstractions.AttemptWorkspaces;
using Harness.Persistence.Abstractions.Foundation;
using Harness.SharedKernel.Identifiers;
using Npgsql;
using NpgsqlTypes;

using Harness.SharedKernel.Security;

namespace Harness.Persistence.Postgres;

public sealed class PostgresAttemptWorkspaceStore(NpgsqlDataSource dataSource) : IAttemptWorkspaceStore
{
    private const string WorkspaceSelect =
        "SELECT tenant_id,project_id,task_id,attempt_id,technical_execution_id,repository_root," +
        "controlled_root,base_reference,branch_name,worktree_path,state,cleanup_state,owner," +
        "fencing_token,lease_expires_at,last_heartbeat_at,commit_sha,session_id,final_error," +
        "created_at,updated_at,released_at,version " +
        "FROM harness.attempt_workspaces WHERE tenant_id=$1 AND attempt_id=$2";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public Task<AttemptWorkspaceReceipt> AcquireAsync(
        AttemptWorkspaceAcquireCommand command,
        CancellationToken cancellationToken = default)
    {
        var normalized = AttemptWorkspaceAcquireValidator.Normalize(command);
        var commandHash = AttemptWorkspaceCommandHash.Compute(normalized);
        return AcquireCoreAsync(normalized, commandHash, cancellationToken);
    }

    public async Task<AttemptWorkspaceSnapshot?> GetAsync(
        string tenantId,
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceAcquireValidator.ValidateUlid(tenantId, nameof(tenantId));
        AttemptWorkspaceAcquireValidator.ValidateUlid(attemptId, nameof(attemptId));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, tenantId, attemptId, forUpdate: false, cancellationToken);
    }

    public Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListExpiredAsync(
        string tenantId,
        DateTimeOffset expiredBefore,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceAcquireValidator.ValidateUlid(tenantId, nameof(tenantId));
        return ListExpiredCoreAsync(tenantId, expiredBefore, cancellationToken);
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
        return HeartbeatCoreAsync(command, cancellationToken);
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
        return ReclaimCoreAsync(command, cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> TransitionAsync(
        AttemptWorkspaceTransitionCommand command,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceTransitionValidator.Validate(command);
        return TransitionCoreAsync(command, cancellationToken);
    }

    public Task<AttemptWorkspaceReceipt> ReleaseAsync(
        AttemptWorkspaceReleaseCommand command,
        CancellationToken cancellationToken = default)
    {
        AttemptWorkspaceReleaseValidator.Validate(command);
        return ReleaseCoreAsync(command, cancellationToken);
    }

    private async Task<AttemptWorkspaceReceipt> AcquireCoreAsync(
        AttemptWorkspaceAcquireCommand command,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"attempt-workspace:{command.TenantId}:{command.ProjectId}"));
        var replayed = await ReadInboxAsync(
            connection, transaction, command.TenantId, command.IdempotencyKey, commandHash, cancellationToken);
        if (replayed)
        {
            var replaySnapshot = await ReadAsync(
                    connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
                ?? throw new InvalidOperationException("An acquire inbox receipt exists without its workspace.");
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, replaySnapshot, []);
        }

        var existing = await ReadAsync(
            connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return MatchesAcquire(existing, command)
                ? new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, existing, [])
                : new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, existing, []);
        }

        var conflicts = await FindScopeConflictsAsync(connection, transaction, command, cancellationToken);
        if (conflicts.Count != 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.ScopeConflict, null, conflicts);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.attempt_workspaces
                (tenant_id, project_id, task_id, attempt_id, repository_root, controlled_root,
                 base_reference, branch_name, worktree_path, state, cleanup_state, owner,
                 fencing_token, lease_expires_at, last_heartbeat_at, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'claimed', 'not_required', $10,
                    1, $11, $12, $12, $12);
            """,
            cancellationToken,
            Text(command.TenantId),
            Text(command.ProjectId),
            Text(command.TaskId),
            Text(command.AttemptId),
            Text(command.RepositoryRoot),
            Text(command.ControlledRoot),
            Text(command.BaseReference),
            Text(command.BranchName),
            Text(command.WorktreePath),
            Text(command.Owner),
            Timestamp(command.OccurredAt + command.LeaseDuration),
            Timestamp(command.OccurredAt));
        foreach (var claim in command.ScopeClaims)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO harness.attempt_scope_claims (id, tenant_id, project_id, attempt_id, path_pattern) " +
                "VALUES ($1, $2, $3, $4, $5);",
                cancellationToken,
                Text(UlidValue.New(command.OccurredAt).ToString()),
                Text(command.TenantId),
                Text(command.ProjectId),
                Text(command.AttemptId),
                Text(claim));
        }

        var snapshot = await ReadAsync(
                connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The acquired workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceClaimed",
            SerializePayload(snapshot),
            command.OccurredAt,
            cancellationToken);
        await WriteInboxAsync(
            connection,
            transaction,
            command.TenantId,
            command.IdempotencyKey,
            commandHash,
            command.AttemptId,
            command.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private async Task<AttemptWorkspaceReceipt> HeartbeatCoreAsync(
        AttemptWorkspaceLeaseCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(
            connection, transaction, command.TenantId, command.AttemptId, forUpdate: true, cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.attempt_workspaces SET lease_expires_at=$1,last_heartbeat_at=$2," +
            "updated_at=$2,version=version+1 WHERE tenant_id=$3 AND attempt_id=$4;",
            cancellationToken,
            Timestamp(command.OccurredAt + command.LeaseDuration),
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.AttemptId));
        await transaction.CommitAsync(cancellationToken);
        var snapshot = await ReadAsync(
                connection, null, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The heartbeated workspace was not persisted.");
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private async Task<AttemptWorkspaceReceipt> ReclaimCoreAsync(
        AttemptWorkspaceReclaimCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(
            connection, transaction, command.TenantId, command.AttemptId, forUpdate: true, cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (current.LeaseExpiresAt > command.OccurredAt)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.attempt_workspaces SET owner=$1,fencing_token=fencing_token+1," +
            "lease_expires_at=$2,last_heartbeat_at=$3,updated_at=$3,version=version+1 " +
            "WHERE tenant_id=$4 AND attempt_id=$5;",
            cancellationToken,
            Text(command.NewOwner),
            Timestamp(command.OccurredAt + command.LeaseDuration),
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.AttemptId));
        var snapshot = await ReadAsync(
                connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The reclaimed workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceReclaimed",
            SerializePayload(snapshot),
            command.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private async Task<AttemptWorkspaceReceipt> TransitionCoreAsync(
        AttemptWorkspaceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(
            connection, transaction, command.TenantId, command.AttemptId, forUpdate: true, cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        if (current.State == command.State &&
            (command.CommitSha is null || current.CommitSha == command.CommitSha) &&
            (command.SessionId is null || current.SessionId == command.SessionId) &&
            (command.TechnicalExecutionId is null ||
                current.TechnicalExecutionId == command.TechnicalExecutionId))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, current, []);
        }

        if (current.State != command.ExpectedState)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.attempt_workspaces SET state=$1,cleanup_state=$2," +
            "commit_sha=COALESCE($3,commit_sha),session_id=COALESCE($4,session_id)," +
            "technical_execution_id=COALESCE($5,technical_execution_id)," +
            "final_error=$6,updated_at=$7,version=version+1 " +
            "WHERE tenant_id=$8 AND attempt_id=$9 AND state=$10;",
            cancellationToken,
            Text(AttemptWorkspaceStateCodec.ToStorage(command.State)),
            Text(AttemptWorkspaceCleanupStateCodec.ToStorage(
                AttemptWorkspaceLifecycle.CleanupStateFor(command.State))),
            NullableText(command.CommitSha),
            NullableText(command.SessionId),
            NullableText(command.TechnicalExecutionId),
            NullableText(command.FinalError),
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.AttemptId),
            Text(AttemptWorkspaceStateCodec.ToStorage(command.ExpectedState)));
        var snapshot = await ReadAsync(
                connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
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
            cancellationToken);
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
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private async Task<AttemptWorkspaceReceipt> ReleaseCoreAsync(
        AttemptWorkspaceReleaseCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadAsync(
            connection, transaction, command.TenantId, command.AttemptId, forUpdate: true, cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.NotFound, null, []);
        }

        if (current.CleanupState == AttemptWorkspaceCleanupState.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.IdempotentReplay, current, []);
        }

        if (!AttemptWorkspaceLifecycle.IsTerminal(current.State))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.InvalidState, current, []);
        }

        if (!OwnsLease(current, command.Owner, command.FencingToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.LeaseRejected, current, []);
        }

        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.attempt_scope_claims SET released_at=$1 " +
            "WHERE tenant_id=$2 AND attempt_id=$3 AND released_at IS NULL;",
            cancellationToken,
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.AttemptId));
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE harness.attempt_workspaces SET cleanup_state='completed',released_at=$1,updated_at=$1," +
            "version=version+1 WHERE tenant_id=$2 AND attempt_id=$3;",
            cancellationToken,
            Timestamp(command.OccurredAt),
            Text(command.TenantId),
            Text(command.AttemptId));
        var snapshot = await ReadAsync(
                connection, transaction, command.TenantId, command.AttemptId, forUpdate: false, cancellationToken)
            ?? throw new InvalidOperationException("The released workspace was not persisted.");
        await AppendAuditAsync(
            connection,
            transaction,
            command.TenantId,
            "attempt.workspaceCleaned",
            SerializePayload(snapshot),
            command.OccurredAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AttemptWorkspaceReceipt(AttemptWorkspaceMutationStatus.Applied, snapshot, []);
    }

    private static async Task<IReadOnlyList<AttemptScopeConflict>> FindScopeConflictsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AttemptWorkspaceAcquireCommand command,
        CancellationToken cancellationToken)
    {
        var activeClaims = new List<(string AttemptId, string Claim)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT attempt_id,path_pattern FROM harness.attempt_scope_claims " +
                "WHERE tenant_id=$1 AND project_id=$2 AND released_at IS NULL " +
                "ORDER BY attempt_id,path_pattern;";
            query.Parameters.Add(Text(command.TenantId));
            query.Parameters.Add(Text(command.ProjectId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                activeClaims.Add((reader.GetString(0).TrimEnd(), reader.GetString(1)));
            }
        }

        return command.ScopeClaims
            .SelectMany(requested => activeClaims
                .Where(existing => AttemptWorkspaceScopePattern.Intersects(requested, existing.Claim))
                .Select(existing => new AttemptScopeConflict(existing.AttemptId, requested, existing.Claim)))
            .ToArray();
    }

    private async Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListExpiredCoreAsync(
        string tenantId,
        DateTimeOffset expiredBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var attemptIds = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText =
                "SELECT attempt_id FROM harness.attempt_workspaces " +
                "WHERE tenant_id=$1 AND released_at IS NULL AND lease_expires_at<=$2 " +
                "ORDER BY lease_expires_at,attempt_id;";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Timestamp(expiredBefore));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                attemptIds.Add(reader.GetString(0).TrimEnd());
            }
        }

        var snapshots = new List<AttemptWorkspaceSnapshot>(attemptIds.Count);
        foreach (var attemptId in attemptIds)
        {
            snapshots.Add(
                await ReadAsync(connection, null, tenantId, attemptId, forUpdate: false, cancellationToken)
                ?? throw new InvalidOperationException("An expired workspace disappeared while listing."));
        }

        return snapshots;
    }

    private static async Task<AttemptWorkspaceSnapshot?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string tenantId,
        string attemptId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        WorkspaceRow row;
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = $"{WorkspaceSelect}{(forUpdate ? " FOR UPDATE" : "")};";
            query.Parameters.Add(Text(tenantId));
            query.Parameters.Add(Text(attemptId));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            row = new WorkspaceRow(
                reader.GetString(0).TrimEnd(),
                reader.GetString(1).TrimEnd(),
                reader.GetString(2).TrimEnd(),
                reader.GetString(3).TrimEnd(),
                reader.IsDBNull(4) ? null : reader.GetString(4).TrimEnd(),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetInt64(13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetFieldValue<DateTimeOffset>(15),
                reader.IsDBNull(16) ? null : reader.GetString(16).TrimEnd(),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.GetFieldValue<DateTimeOffset>(19),
                reader.GetFieldValue<DateTimeOffset>(20),
                reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
                reader.GetInt64(22));
        }

        var claims = new List<AttemptScopeClaimSnapshot>();
        await using var claimQuery = connection.CreateCommand();
        claimQuery.Transaction = transaction;
        claimQuery.CommandText =
            "SELECT id,path_pattern,released_at FROM harness.attempt_scope_claims " +
            "WHERE tenant_id=$1 AND attempt_id=$2 ORDER BY path_pattern;";
        claimQuery.Parameters.Add(Text(tenantId));
        claimQuery.Parameters.Add(Text(attemptId));
        await using var claimReader = await claimQuery.ExecuteReaderAsync(cancellationToken);
        while (await claimReader.ReadAsync(cancellationToken))
        {
            claims.Add(new AttemptScopeClaimSnapshot(
                claimReader.GetString(0).TrimEnd(),
                claimReader.GetString(1),
                claimReader.IsDBNull(2) ? null : claimReader.GetFieldValue<DateTimeOffset>(2)));
        }

        return new AttemptWorkspaceSnapshot
        {
            TenantId = row.TenantId,
            ProjectId = row.ProjectId,
            TaskId = row.TaskId,
            AttemptId = row.AttemptId,
            TechnicalExecutionId = row.TechnicalExecutionId,
            RepositoryRoot = row.RepositoryRoot,
            ControlledRoot = row.ControlledRoot,
            BaseReference = row.BaseReference,
            BranchName = row.BranchName,
            WorktreePath = row.WorktreePath,
            State = AttemptWorkspaceStateCodec.Parse(row.State),
            CleanupState = AttemptWorkspaceCleanupStateCodec.Parse(row.CleanupState),
            ScopeClaims = claims,
            Owner = row.Owner,
            FencingToken = row.FencingToken,
            LeaseExpiresAt = row.LeaseExpiresAt,
            LastHeartbeatAt = row.LastHeartbeatAt,
            CommitSha = row.CommitSha,
            SessionId = row.SessionId,
            FinalError = row.FinalError,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            ReleasedAt = row.ReleasedAt,
            Version = row.Version,
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
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            "SELECT message_hash FROM harness.inbox_messages WHERE tenant_id=$1 AND idempotency_key=$2;";
        query.Parameters.Add(Text(tenantId));
        query.Parameters.Add(Text(idempotencyKey));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        if (!string.Equals(reader.GetString(0).TrimEnd(), commandHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(
                "The idempotency key belongs to a different attempt workspace command.");
        }

        return true;
    }

    private static Task WriteInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string idempotencyKey,
        string commandHash,
        string attemptId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.inbox_messages (tenant_id,idempotency_key,message_hash,response_json,processed_at) " +
            "VALUES ($1,$2,$3,$4,$5);",
            cancellationToken,
            Text(tenantId),
            Text(idempotencyKey),
            Text(commandHash),
            Json(JsonSerializer.Serialize(new AttemptWorkspaceInboxReceipt(attemptId), JsonOptions)),
            Timestamp(occurredAt));

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        payload = PersistenceSanitizer.SanitizeJson(payload);
        await ExecuteAsync(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0));",
            cancellationToken,
            Text($"audit-ledger:{tenantId}"));
        var (sequence, previous) = await ReadLedgerTailAsync(connection, transaction, tenantId, cancellationToken);
        var hash = AuditLedgerHash.Compute(previous, tenantId, sequence, eventType, payload, occurredAt);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO harness.audit_ledger
                (id, tenant_id, sequence, previous_hash, event_hash, event_type, payload_json, occurred_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
            """,
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Bigint(sequence),
            Text(previous),
            Text(hash),
            Text(eventType),
            Json(payload),
            Timestamp(occurredAt));
    }

    private static Task AppendOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string eventType,
        string payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO harness.outbox_messages (id, tenant_id, event_type, payload_json, occurred_at) VALUES ($1, $2, $3, $4, $5);",
            cancellationToken,
            Text(UlidValue.New(occurredAt).ToString()),
            Text(tenantId),
            Text(eventType),
            Json(PersistenceSanitizer.SanitizeJson(payload)),
            Timestamp(occurredAt));

    private static async Task<(long Sequence, string PreviousHash)> ReadLedgerTailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var tail = connection.CreateCommand();
        tail.Transaction = transaction;
        tail.CommandText =
            """
            SELECT sequence, event_hash FROM harness.audit_ledger
            WHERE tenant_id = $1 ORDER BY sequence DESC LIMIT 1 FOR UPDATE;
            """;
        tail.Parameters.Add(Text(tenantId));
        await using var reader = await tail.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0) + 1, reader.GetString(1).TrimEnd())
            : (1, AuditLedgerHash.Genesis);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter<string> Text(string value) => new() { TypedValue = value };

    private static NpgsqlParameter<long> Bigint(long value) => new() { TypedValue = value };

    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new() { TypedValue = value };

    private static NpgsqlParameter NullableText(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static NpgsqlParameter<string> Json(string value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Jsonb,
        TypedValue = value,
    };

    private sealed record WorkspaceRow(
        string TenantId,
        string ProjectId,
        string TaskId,
        string AttemptId,
        string? TechnicalExecutionId,
        string RepositoryRoot,
        string ControlledRoot,
        string BaseReference,
        string BranchName,
        string WorktreePath,
        string State,
        string CleanupState,
        string Owner,
        long FencingToken,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset LastHeartbeatAt,
        string? CommitSha,
        string? SessionId,
        string? FinalError,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ReleasedAt,
        long Version);

    private sealed record AttemptWorkspaceInboxReceipt(string AttemptId);
}
