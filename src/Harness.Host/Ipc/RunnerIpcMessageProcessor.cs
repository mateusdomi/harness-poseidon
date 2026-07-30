using System.Text.Json;
using Harness.Modules.Coordination.Application;
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

        // B5/F13 — conclusão EXATAMENTE UMA VEZ, inclusive em falha.
        //
        // O store já recusa mensagem para tentativa concluída, e essa garantia atômica continua
        // sendo a última palavra. A política entra antes por dois motivos: ela distingue o REENVIO
        // fiel (mesma chave, mesmo desfecho — a rede é falível e o agente tem direito de repetir
        // sem medo) de uma segunda história diferente, e devolve o motivo em vez de um conflito
        // genérico. Sem essa distinção, um agente que repetiu a conclusão por timeout de rede via
        // a própria entrega recusada.
        if (RunnerMessageTypes.EndsTurn(message.Type))
        {
            var completionFailure = await DecideCompletionAsync(message, cancellationToken);
            if (completionFailure is not null)
            {
                return RunnerIpcProcessResult.Failed(completionFailure);
            }
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

    /// <summary>
    /// Aplica a política de conclusão sobre o estado durável da tentativa. Devolve falha só quando
    /// a tentativa já foi concluída por um desfecho DIFERENTE: aí a segunda versão da história não
    /// pode reescrever a primeira. Reenvio idêntico segue adiante e o store o trata como repetição.
    /// </summary>
    private async Task<RunnerIpcFailure?> DecideCompletionAsync(
        RunnerMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        var attempt = await _store.ReadAttemptAsync(message.AttemptId, cancellationToken);
        if (attempt is null)
        {
            return null;
        }

        var kind = RunnerMessageTypes.Canonical(message.Type) == RunnerMessageTypes.Escalation
            ? "escalated"
            : "succeeded";
        var decision = TurnCompletionPolicy.Report(
            new TurnCompletionState(
                message.AttemptId,
                attempt.Completed,
                attempt.Completed ? kind : null,
                attempt.Completed ? message.IdempotencyKey : null),
            kind,
            message.IdempotencyKey);

        return decision.Outcome == TurnCompletionOutcome.AlreadyCompleted
            ? Conflict(
                "attempt_already_completed",
                "A tentativa já foi concluída por outro desfecho; a conclusão vale uma vez só.")
            : null;
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
