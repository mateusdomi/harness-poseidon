using System.Text.Json;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.SharedKernel.RunnerIpc;
using Harness.SharedKernel.Time;

namespace Harness.Host.Ipc;

public sealed class RunnerIpcMessageProcessor(IRunnerMessageStore store, IClock clock)
{
    private readonly IRunnerMessageStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<RunnerIpcProcessResult> ProcessAsync(
        RunnerMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var validationFailure = Validate(message);
        if (validationFailure is not null)
        {
            return RunnerIpcProcessResult.Failed(validationFailure);
        }

        var result = await _store.ApplyAsync(message, _clock.UtcNow, cancellationToken);
        return result.Rejection == RunnerMessageRejection.None
            ? RunnerIpcProcessResult.Succeeded(
                result.Receipt ?? throw new InvalidOperationException("Runner store returned no receipt."))
            : RunnerIpcProcessResult.Failed(MapRejection(result));
    }

    public async Task<RunnerAttemptSnapshot?> ReadAttemptAsync(
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        var state = await _store.ReadAttemptAsync(attemptId, cancellationToken);
        return state is null
            ? null
            : new RunnerAttemptSnapshot(
                state.RunnerId,
                state.AttemptId,
                state.LastSequence,
                state.HeartbeatCount,
                state.CheckpointIds,
                state.Completed,
                state.InboxCount,
                state.OutboxCount,
                state.Version);
    }

    private static RunnerIpcFailure? Validate(RunnerMessageEnvelope message)
    {
        if (!IsIdentifierValid(message.RunnerId) || !IsIdentifierValid(message.AttemptId))
        {
            return BadRequest("invalid_runner_identity", "runnerId and attemptId are required and bounded.");
        }

        if (message.Sequence <= 0)
        {
            return BadRequest("invalid_runner_sequence", "sequence must be positive.");
        }

        if (string.IsNullOrWhiteSpace(message.IdempotencyKey) || message.IdempotencyKey.Length > 200)
        {
            return BadRequest("invalid_idempotency_key", "idempotencyKey is required and bounded.");
        }

        if (!RunnerMessageTypes.All.Contains(message.Type))
        {
            return BadRequest("invalid_runner_message_type", "The runner message type is unsupported.");
        }

        if (message.Payload.ValueKind is not JsonValueKind.Object)
        {
            return BadRequest("invalid_runner_payload", "payload must be a JSON object.");
        }

        return ValidatePayload(message);
    }

    private static RunnerIpcFailure? ValidatePayload(RunnerMessageEnvelope message)
    {
        if (message.Type != RunnerMessageTypes.Checkpoint)
        {
            return null;
        }

        if (!message.Payload.TryGetProperty("checkpointId", out var checkpointId) ||
            checkpointId.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(checkpointId.GetString()) ||
            checkpointId.GetString()!.Length > 200)
        {
            return BadRequest(
                "invalid_checkpoint_payload",
                "checkpoint payload requires a checkpointId with at most 200 characters.");
        }

        return null;
    }

    private static RunnerIpcFailure MapRejection(RunnerMessageStoreResult result) => result.Rejection switch
    {
        RunnerMessageRejection.IdempotencyKeyConflict => Conflict(
            "idempotency_key_conflict",
            "The idempotency key was already used by a different message."),
        RunnerMessageRejection.StaleSequence => Conflict(
            "stale_runner_sequence",
            $"Expected runner sequence {result.ExpectedSequence}."),
        RunnerMessageRejection.SequenceGap => Conflict(
            "runner_sequence_gap",
            $"Expected runner sequence {result.ExpectedSequence}."),
        RunnerMessageRejection.AttemptAlreadyCompleted => Conflict(
            "attempt_already_completed",
            "The attempt does not accept new runner messages."),
        RunnerMessageRejection.RunnerOwnerConflict => Conflict(
            "runner_owner_conflict",
            "The attempt is already owned by a different runner."),
        RunnerMessageRejection.StaleFencingToken => Conflict(
            "stale_fencing_token",
            "The message carries a fencing token superseded by the active dispatch."),
        _ => throw new ArgumentOutOfRangeException(
            nameof(result),
            result.Rejection,
            "Unknown Runner message rejection."),
    };

    private static bool IsIdentifierValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsWhiteSpace);

    private static RunnerIpcFailure BadRequest(string title, string detail) =>
        new(StatusCodes.Status400BadRequest, title, detail);

    private static RunnerIpcFailure Conflict(string title, string detail) =>
        new(StatusCodes.Status409Conflict, title, detail);
}

public sealed record RunnerIpcFailure(int StatusCode, string Title, string Detail);

public sealed record RunnerIpcProcessResult(RunnerMessageReceipt? Receipt, RunnerIpcFailure? Failure)
{
    public static RunnerIpcProcessResult Succeeded(RunnerMessageReceipt receipt) => new(receipt, null);

    public static RunnerIpcProcessResult Failed(RunnerIpcFailure failure) => new(null, failure);
}
