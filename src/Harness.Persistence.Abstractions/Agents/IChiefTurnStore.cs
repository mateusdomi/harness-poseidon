using Harness.Persistence.Abstractions.Conversations;

namespace Harness.Persistence.Abstractions.Agents;

public interface IChiefTurnStore
{
    Task<ChiefTurnRecord> EnqueueAsync(ChiefTurnEnqueueCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnLease> AcquireAsync(ChiefTurnAcquireCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnLease?> AcquireNextAsync(
        string ownerId, DateTimeOffset now, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
    Task CompleteAsync(ChiefTurnCompleteCommand command, CancellationToken cancellationToken = default);
    Task FailAsync(ChiefTurnFailCommand command, CancellationToken cancellationToken = default);
    Task<ChiefTurnRecord?> GetAsync(string tenantId, string turnId, CancellationToken cancellationToken = default);
}

public sealed record ChiefTurnRecord(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string UserMessageId, string State, int AttemptCount, string? SessionId,
    string? ResponseMessageId, string? LastErrorCode, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ChiefTurnEnqueueCommand(
    string TenantId, string ProjectId, string ConversationId, string TurnId,
    string ChiefAgentId, MessageRecord UserMessage, string IdempotencyKey,
    DateTimeOffset OccurredAt);

public sealed record ChiefTurnAcquireCommand(
    string TenantId, string TurnId, string OwnerId, DateTimeOffset Now, TimeSpan LeaseDuration);

public sealed record ChiefTurnLease(
    ChiefTurnRecord Turn, string OwnerId, long FencingToken, DateTimeOffset ExpiresAt,
    string ChiefAgentId, string Instruction, string? SessionId);

public sealed record ChiefTurnCompleteCommand(
    ChiefTurnLease Lease, MessageRecord ChiefMessage, IReadOnlyList<string> Chunks,
    string SessionId, string StatusDigestJson, DateTimeOffset OccurredAt);

public sealed record ChiefTurnFailCommand(
    ChiefTurnLease Lease, string ErrorCode, DateTimeOffset OccurredAt, bool Retryable);

public sealed class ChiefTurnConflictException(string message) : Exception(message);
