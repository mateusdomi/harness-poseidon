using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.SharedKernel.Identifiers;

namespace Harness.Persistence.Abstractions.AttemptWorkspaces;

public interface IAttemptWorkspaceStore
{
    Task<AttemptWorkspaceReceipt> AcquireAsync(
        AttemptWorkspaceAcquireCommand command,
        CancellationToken cancellationToken = default);

    Task<AttemptWorkspaceSnapshot?> GetAsync(
        string tenantId,
        string attemptId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttemptWorkspaceSnapshot>> ListExpiredAsync(
        string tenantId,
        DateTimeOffset expiredBefore,
        CancellationToken cancellationToken = default);

    Task<AttemptWorkspaceReceipt> HeartbeatAsync(
        AttemptWorkspaceLeaseCommand command,
        CancellationToken cancellationToken = default);

    Task<AttemptWorkspaceReceipt> ReclaimExpiredAsync(
        AttemptWorkspaceReclaimCommand command,
        CancellationToken cancellationToken = default);

    Task<AttemptWorkspaceReceipt> TransitionAsync(
        AttemptWorkspaceTransitionCommand command,
        CancellationToken cancellationToken = default);

    Task<AttemptWorkspaceReceipt> ReleaseAsync(
        AttemptWorkspaceReleaseCommand command,
        CancellationToken cancellationToken = default);
}

public enum AttemptWorkspaceState
{
    Claimed,
    Prepared,
    Running,
    Completed,
    Failed,
}

public enum AttemptWorkspaceCleanupState
{
    NotRequired,
    Pending,
    Completed,
}

public enum AttemptWorkspaceMutationStatus
{
    Applied,
    IdempotentReplay,
    NotFound,
    InvalidState,
    LeaseRejected,
    ScopeConflict,
}

public sealed record AttemptScopeClaimSnapshot(
    string ClaimId,
    string PathPattern,
    DateTimeOffset? ReleasedAt);

public sealed record AttemptScopeConflict(
    string ExistingAttemptId,
    string RequestedPathPattern,
    string ExistingPathPattern);

public sealed record AttemptWorkspaceSnapshot
{
    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    public string? TechnicalExecutionId { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string ControlledRoot { get; init; }

    public required string BaseReference { get; init; }

    public required string BranchName { get; init; }

    public required string WorktreePath { get; init; }

    public required AttemptWorkspaceState State { get; init; }

    public required AttemptWorkspaceCleanupState CleanupState { get; init; }

    public required IReadOnlyList<AttemptScopeClaimSnapshot> ScopeClaims { get; init; }

    public required string Owner { get; init; }

    public required long FencingToken { get; init; }

    public required DateTimeOffset LeaseExpiresAt { get; init; }

    public required DateTimeOffset LastHeartbeatAt { get; init; }

    public string? CommitSha { get; init; }

    public string? SessionId { get; init; }

    public string? FinalError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }

    public required long Version { get; init; }
}

public sealed record AttemptWorkspaceReceipt(
    AttemptWorkspaceMutationStatus Status,
    AttemptWorkspaceSnapshot? Workspace,
    IReadOnlyList<AttemptScopeConflict> Conflicts)
{
    public bool Succeeded =>
        Status is AttemptWorkspaceMutationStatus.Applied or AttemptWorkspaceMutationStatus.IdempotentReplay;
}

public sealed record AttemptWorkspaceAcquireCommand
{
    public required string TenantId { get; init; }

    public required string ProjectId { get; init; }

    public required string TaskId { get; init; }

    public required string AttemptId { get; init; }

    public required string RepositoryRoot { get; init; }

    public required string ControlledRoot { get; init; }

    public required string BaseReference { get; init; }

    public required string BranchName { get; init; }

    public required string WorktreePath { get; init; }

    public required IReadOnlyList<string> ScopeClaims { get; init; }

    public required string Owner { get; init; }

    public required TimeSpan LeaseDuration { get; init; }

    public required string IdempotencyKey { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed record AttemptWorkspaceLeaseCommand(
    string TenantId,
    string AttemptId,
    string Owner,
    long FencingToken,
    TimeSpan LeaseDuration,
    DateTimeOffset OccurredAt);

public sealed record AttemptWorkspaceReclaimCommand(
    string TenantId,
    string AttemptId,
    string NewOwner,
    TimeSpan LeaseDuration,
    DateTimeOffset OccurredAt);

public sealed record AttemptWorkspaceTransitionCommand
{
    public required string TenantId { get; init; }

    public required string AttemptId { get; init; }

    public required string Owner { get; init; }

    public required long FencingToken { get; init; }

    public required AttemptWorkspaceState ExpectedState { get; init; }

    public required AttemptWorkspaceState State { get; init; }

    public string? CommitSha { get; init; }

    public string? SessionId { get; init; }

    public string? TechnicalExecutionId { get; init; }

    public string? FinalError { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed record AttemptWorkspaceReleaseCommand(
    string TenantId,
    string AttemptId,
    string Owner,
    long FencingToken,
    DateTimeOffset OccurredAt);

public static class AttemptWorkspaceStateCodec
{
    public static string ToStorage(AttemptWorkspaceState state) => state switch
    {
        AttemptWorkspaceState.Claimed => "claimed",
        AttemptWorkspaceState.Prepared => "prepared",
        AttemptWorkspaceState.Running => "running",
        AttemptWorkspaceState.Completed => "completed",
        AttemptWorkspaceState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown attempt workspace state."),
    };

    public static AttemptWorkspaceState Parse(string value) => value switch
    {
        "claimed" => AttemptWorkspaceState.Claimed,
        "prepared" => AttemptWorkspaceState.Prepared,
        "running" => AttemptWorkspaceState.Running,
        "completed" => AttemptWorkspaceState.Completed,
        "failed" => AttemptWorkspaceState.Failed,
        _ => throw new ArgumentException("Unknown attempt workspace state value.", nameof(value)),
    };
}

public static class AttemptWorkspaceCleanupStateCodec
{
    public static string ToStorage(AttemptWorkspaceCleanupState state) => state switch
    {
        AttemptWorkspaceCleanupState.NotRequired => "not_required",
        AttemptWorkspaceCleanupState.Pending => "pending",
        AttemptWorkspaceCleanupState.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown attempt workspace cleanup state."),
    };

    public static AttemptWorkspaceCleanupState Parse(string value) => value switch
    {
        "not_required" => AttemptWorkspaceCleanupState.NotRequired,
        "pending" => AttemptWorkspaceCleanupState.Pending,
        "completed" => AttemptWorkspaceCleanupState.Completed,
        _ => throw new ArgumentException("Unknown attempt workspace cleanup state value.", nameof(value)),
    };
}

public static class AttemptWorkspaceLifecycle
{
    private static readonly HashSet<(AttemptWorkspaceState From, AttemptWorkspaceState To)> AllowedTransitions =
    [
        (AttemptWorkspaceState.Claimed, AttemptWorkspaceState.Prepared),
        (AttemptWorkspaceState.Claimed, AttemptWorkspaceState.Failed),
        (AttemptWorkspaceState.Prepared, AttemptWorkspaceState.Running),
        (AttemptWorkspaceState.Prepared, AttemptWorkspaceState.Failed),
        (AttemptWorkspaceState.Running, AttemptWorkspaceState.Completed),
        (AttemptWorkspaceState.Running, AttemptWorkspaceState.Failed),
    ];

    public static bool IsTransitionAllowed(AttemptWorkspaceState from, AttemptWorkspaceState to) =>
        AllowedTransitions.Contains((from, to));

    public static bool IsTerminal(AttemptWorkspaceState state) =>
        state is AttemptWorkspaceState.Completed or AttemptWorkspaceState.Failed;

    public static AttemptWorkspaceCleanupState CleanupStateFor(AttemptWorkspaceState state) =>
        IsTerminal(state) ? AttemptWorkspaceCleanupState.Pending : AttemptWorkspaceCleanupState.NotRequired;
}

public static class AttemptWorkspaceScopePattern
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathRooted(value))
        {
            throw new ArgumentException("Scope claims must be workspace-relative.", nameof(value));
        }

        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 ||
            normalized.Split('/').Any(segment => segment is "." or ".." or "") ||
            (normalized.Contains('*') && !normalized.EndsWith("/**", StringComparison.Ordinal)) ||
            normalized[..Math.Max(0, normalized.Length - 3)].Contains('*'))
        {
            throw new ArgumentException("Scope claims may use only a trailing /** wildcard.", nameof(value));
        }

        return normalized;
    }

    public static bool Intersects(string left, string right)
    {
        var leftBase = BasePath(left);
        var rightBase = BasePath(right);
        return string.Equals(leftBase, rightBase, StringComparison.OrdinalIgnoreCase) ||
            leftBase.StartsWith($"{rightBase}/", StringComparison.OrdinalIgnoreCase) ||
            rightBase.StartsWith($"{leftBase}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BasePath(string pattern) =>
        pattern.EndsWith("/**", StringComparison.Ordinal) ? pattern[..^3].TrimEnd('/') : pattern;
}

public static class AttemptWorkspaceErrorSanitizer
{
    public const int MaximumLength = 2000;

    public static string Sanitize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var builder = new StringBuilder(Math.Min(value.Length, MaximumLength));
        foreach (var character in value)
        {
            if (builder.Length == MaximumLength)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0
            ? throw new ArgumentException("A sanitized error requires printable content.", nameof(value))
            : sanitized;
    }
}

public static class AttemptWorkspaceAcquireValidator
{
    public static AttemptWorkspaceAcquireCommand Normalize(AttemptWorkspaceAcquireCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUlid(command.TenantId, nameof(command));
        ValidateUlid(command.ProjectId, nameof(command));
        ValidateUlid(command.TaskId, nameof(command));
        ValidateUlid(command.AttemptId, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ControlledRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.BaseReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.BranchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorktreePath);
        ArgumentNullException.ThrowIfNull(command.ScopeClaims);
        ValidateOwner(command.Owner, nameof(command));
        ValidateLeaseDuration(command.LeaseDuration, nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.IdempotencyKey);
        if (!command.BranchName.StartsWith("task/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Attempt workspace branches must use the task/ prefix.", nameof(command));
        }

        var repository = Path.GetFullPath(command.RepositoryRoot);
        var controlledRoot = Path.GetFullPath(command.ControlledRoot);
        var worktree = Path.GetFullPath(command.WorktreePath);
        if (repository.Contains(',') || controlledRoot.Contains(',') || worktree.Contains(','))
        {
            throw new ArgumentException("Workspace paths must be mount-safe.", nameof(command));
        }

        if (repository == worktree)
        {
            throw new ArgumentException("Repository and worktree paths must be distinct.", nameof(command));
        }

        if (!worktree.StartsWith($"{controlledRoot}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The worktree path must live inside the controlled root.", nameof(command));
        }

        var claims = command.ScopeClaims
            .Select(AttemptWorkspaceScopePattern.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (claims.Length == 0)
        {
            throw new ArgumentException("At least one scope claim is required.", nameof(command));
        }

        return command with
        {
            RepositoryRoot = repository,
            ControlledRoot = controlledRoot,
            WorktreePath = worktree,
            ScopeClaims = claims,
        };
    }

    public static void ValidateUlid(string value, string parameterName)
    {
        if (!UlidValue.TryParse(value, out _))
        {
            throw new ArgumentException("Value must be a canonical ULID.", parameterName);
        }
    }

    public static void ValidateOwner(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200)
        {
            throw new ArgumentException("Owner must contain at most 200 characters.", parameterName);
        }
    }

    public static void ValidateLeaseDuration(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("Lease duration must be positive and at most 24 hours.", parameterName);
        }
    }
}

public static class AttemptWorkspaceTransitionValidator
{
    public static void Validate(AttemptWorkspaceTransitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.TenantId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.AttemptId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateOwner(command.Owner, nameof(command));
        if (command.FencingToken <= 0)
        {
            throw new ArgumentException("Fencing token must be positive.", nameof(command));
        }

        if (!AttemptWorkspaceLifecycle.IsTransitionAllowed(command.ExpectedState, command.State))
        {
            throw new ArgumentException("The workspace lifecycle transition is not allowed.", nameof(command));
        }

        if (command.State == AttemptWorkspaceState.Prepared &&
            (command.CommitSha is null || command.CommitSha.Length != 40))
        {
            throw new ArgumentException("A prepared workspace requires a 40-character commit SHA.", nameof(command));
        }

        if (command.State == AttemptWorkspaceState.Running && string.IsNullOrWhiteSpace(command.SessionId))
        {
            throw new ArgumentException("A running workspace requires an executor session id.", nameof(command));
        }

        if (command.TechnicalExecutionId is not null)
        {
            AttemptWorkspaceAcquireValidator.ValidateUlid(command.TechnicalExecutionId, nameof(command));
        }

        if (command.State == AttemptWorkspaceState.Failed && string.IsNullOrWhiteSpace(command.FinalError))
        {
            throw new ArgumentException("A failed workspace requires a sanitized final error.", nameof(command));
        }

        if (command.State != AttemptWorkspaceState.Failed && command.FinalError is not null)
        {
            throw new ArgumentException("Only a failed workspace records a final error.", nameof(command));
        }

        if (command.FinalError is not null &&
            !string.Equals(
                command.FinalError,
                AttemptWorkspaceErrorSanitizer.Sanitize(command.FinalError),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The final error must already be sanitized.", nameof(command));
        }
    }
}

public static class AttemptWorkspaceReleaseValidator
{
    public static void Validate(AttemptWorkspaceReleaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.TenantId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateUlid(command.AttemptId, nameof(command));
        AttemptWorkspaceAcquireValidator.ValidateOwner(command.Owner, nameof(command));
        if (command.FencingToken <= 0)
        {
            throw new ArgumentException("Fencing token must be positive.", nameof(command));
        }
    }
}

public static class AttemptWorkspaceCommandHash
{
    public static string Compute(AttemptWorkspaceAcquireCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new AcquireIdentity(
                command.TenantId,
                command.ProjectId,
                command.TaskId,
                command.AttemptId,
                command.RepositoryRoot,
                command.ControlledRoot,
                command.BaseReference,
                command.BranchName,
                command.WorktreePath,
                command.ScopeClaims))));
    }

    private sealed record AcquireIdentity(
        string TenantId,
        string ProjectId,
        string TaskId,
        string AttemptId,
        string RepositoryRoot,
        string ControlledRoot,
        string BaseReference,
        string BranchName,
        string WorktreePath,
        IReadOnlyList<string> ScopeClaims);
}
