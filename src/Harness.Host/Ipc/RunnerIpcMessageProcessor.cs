using System.Security.Cryptography;
using System.Text.Json;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.Host.Ipc;

public sealed class RunnerIpcMessageProcessor
{
    private readonly object _sync = new();
    private readonly Dictionary<string, InboxEntry> _inbox = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public RunnerIpcProcessResult Process(RunnerMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var validationFailure = Validate(message);
        if (validationFailure is not null)
        {
            return RunnerIpcProcessResult.Failed(validationFailure);
        }

        var fingerprint = Fingerprint(message);
        lock (_sync)
        {
            if (_inbox.TryGetValue(message.IdempotencyKey, out var inboxEntry))
            {
                if (!string.Equals(inboxEntry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return RunnerIpcProcessResult.Failed(
                        new RunnerIpcFailure(
                            StatusCodes.Status409Conflict,
                            "idempotency_key_conflict",
                            "The idempotency key was already used by a different message."));
                }

                return RunnerIpcProcessResult.Succeeded(inboxEntry.Receipt with { Applied = false, Replay = true });
            }

            _attempts.TryGetValue(message.AttemptId, out var state);
            var expectedSequence = (state?.LastSequence ?? 0) + 1;
            if (message.Sequence != expectedSequence)
            {
                var title = message.Sequence < expectedSequence ? "stale_runner_sequence" : "runner_sequence_gap";
                return RunnerIpcProcessResult.Failed(
                    new RunnerIpcFailure(
                        StatusCodes.Status409Conflict,
                        title,
                        $"Expected runner sequence {expectedSequence}."));
            }

            if (state is not null && state.Completed)
            {
                return RunnerIpcProcessResult.Failed(
                    new RunnerIpcFailure(
                        StatusCodes.Status409Conflict,
                        "attempt_already_completed",
                        "The attempt does not accept new runner messages."));
            }

            if (state is not null && !string.Equals(state.RunnerId, message.RunnerId, StringComparison.Ordinal))
            {
                return RunnerIpcProcessResult.Failed(
                    new RunnerIpcFailure(
                        StatusCodes.Status409Conflict,
                        "runner_owner_conflict",
                        "The attempt is already owned by a different runner."));
            }

            var payloadFailure = ValidatePayload(message);
            if (payloadFailure is not null)
            {
                return RunnerIpcProcessResult.Failed(payloadFailure);
            }

            state ??= new AttemptState(message.RunnerId, message.AttemptId);
            Apply(state, message);
            _attempts[message.AttemptId] = state;

            var receipt = new RunnerMessageReceipt(message.AttemptId, message.Sequence, Applied: true, Replay: false);
            _inbox.Add(message.IdempotencyKey, new InboxEntry(fingerprint, receipt));
            state.InboxCount++;
            return RunnerIpcProcessResult.Succeeded(receipt);
        }
    }

    public RunnerAttemptSnapshot? ReadAttempt(string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        lock (_sync)
        {
            return _attempts.TryGetValue(attemptId, out var state)
                ? new RunnerAttemptSnapshot(
                    state.RunnerId,
                    state.AttemptId,
                    state.LastSequence,
                    state.HeartbeatCount,
                    state.CheckpointIds.ToArray(),
                    state.Completed,
                    state.InboxCount)
                : null;
        }
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

        return null;
    }

    private static RunnerIpcFailure? ValidatePayload(RunnerMessageEnvelope message)
    {
        if (message.Type == RunnerMessageTypes.Checkpoint &&
            (!message.Payload.TryGetProperty("checkpointId", out var checkpointId) ||
                checkpointId.ValueKind is not JsonValueKind.String ||
                string.IsNullOrWhiteSpace(checkpointId.GetString())))
        {
            return BadRequest("invalid_checkpoint_payload", "checkpoint payload requires checkpointId.");
        }

        return null;
    }

    private static void Apply(AttemptState state, RunnerMessageEnvelope message)
    {
        state.LastSequence = message.Sequence;
        switch (message.Type)
        {
            case RunnerMessageTypes.Heartbeat:
                state.HeartbeatCount++;
                break;
            case RunnerMessageTypes.Checkpoint:
                state.CheckpointIds.Add(message.Payload.GetProperty("checkpointId").GetString()!);
                break;
            case RunnerMessageTypes.Completion:
                state.Completed = true;
                break;
        }
    }

    private static bool IsIdentifierValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 100 && !value.Any(char.IsWhiteSpace);

    private static RunnerIpcFailure BadRequest(string title, string detail) =>
        new(StatusCodes.Status400BadRequest, title, detail);

    private static string Fingerprint(RunnerMessageEnvelope message) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message)));

    private sealed record InboxEntry(string Fingerprint, RunnerMessageReceipt Receipt);

    private sealed class AttemptState(string runnerId, string attemptId)
    {
        public string RunnerId { get; } = runnerId;

        public string AttemptId { get; } = attemptId;

        public long LastSequence { get; set; }

        public int HeartbeatCount { get; set; }

        public List<string> CheckpointIds { get; } = [];

        public bool Completed { get; set; }

        public int InboxCount { get; set; }
    }
}

public sealed record RunnerIpcFailure(int StatusCode, string Title, string Detail);

public sealed record RunnerIpcProcessResult(RunnerMessageReceipt? Receipt, RunnerIpcFailure? Failure)
{
    public static RunnerIpcProcessResult Succeeded(RunnerMessageReceipt receipt) => new(receipt, null);

    public static RunnerIpcProcessResult Failed(RunnerIpcFailure failure) => new(null, failure);
}
