using System.Text.Json;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.IntegrationTests.Persistence;

internal static class RunnerMessageStoreBehavior
{
    public static async Task AssertAsync(
        IRunnerMessageStore store,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var occurredAt = new DateTimeOffset(2026, 7, 18, 14, 0, 0, TimeSpan.Zero);
        var heartbeat = Message(
            "runner-dual",
            attemptId,
            1,
            "dual:1",
            RunnerMessageTypes.Heartbeat,
            new { observedAt = occurredAt },
            fencingToken: 5);
        var first = await store.ApplyAsync(heartbeat, occurredAt, cancellationToken);
        Assert.True(first.Receipt?.Applied);
        Assert.Equal(RunnerMessageRejection.None, first.Rejection);

        var replay = await store.ApplyAsync(heartbeat, occurredAt, cancellationToken);
        Assert.True(replay.Receipt?.Replay);
        Assert.False(replay.Receipt?.Applied);

        var conflict = heartbeat with
        {
            Payload = JsonSerializer.SerializeToElement(new { observedAt = occurredAt.AddSeconds(1) }),
        };
        var conflictResult = await store.ApplyAsync(conflict, occurredAt, cancellationToken);
        Assert.Equal(RunnerMessageRejection.IdempotencyKeyConflict, conflictResult.Rejection);

        var checkpoint = Message(
            "runner-dual",
            attemptId,
            2,
            "dual:2",
            RunnerMessageTypes.Checkpoint,
            new { checkpointId = "commit-dual" },
            fencingToken: 5);
        var concurrentCheckpoint = await Task.WhenAll(
            store.ApplyAsync(checkpoint, occurredAt.AddSeconds(1), cancellationToken),
            store.ApplyAsync(checkpoint, occurredAt.AddSeconds(1), cancellationToken));
        Assert.Single(concurrentCheckpoint, result => result.Receipt?.Applied == true);
        Assert.Single(concurrentCheckpoint, result => result.Receipt?.Replay == true);

        var gap = await store.ApplyAsync(
            Message(
                "runner-dual",
                attemptId,
                4,
                "dual:4",
                RunnerMessageTypes.Heartbeat,
                new { observedAt = occurredAt.AddSeconds(3) }),
            occurredAt.AddSeconds(3),
            cancellationToken);
        Assert.Equal(RunnerMessageRejection.SequenceGap, gap.Rejection);
        Assert.Equal(3, gap.ExpectedSequence);

        // B5 — a posse da tentativa NÃO é do processo. Este bloco descrevia a regra antiga
        // (`runner_owner_conflict`), que recusava a mensagem quando o identificador do processo
        // mudava. Era justamente o agente REINICIADO que ela punia: identificador novo, trabalho
        // feito, conclusão jogada fora. Agora o identificador é roteamento e quem decide é o
        // fencing — e é ele que continua barrando o resultado tardio de uma tentativa superada.
        var staleFencing = await store.ApplyAsync(
            Message(
                "runner-other",
                attemptId,
                3,
                "dual:stale-fencing",
                RunnerMessageTypes.Completion,
                new { outcome = "completed" },
                fencingToken: 1),
            occurredAt.AddSeconds(2),
            cancellationToken);
        Assert.Equal(RunnerMessageRejection.StaleFencingToken, staleFencing.Rejection);

        // Mesmo fencing, processo diferente: é o agente que voltou, e ele conclui a própria
        // tentativa.
        var completion = await store.ApplyAsync(
            Message(
                "runner-apos-reinicio",
                attemptId,
                3,
                "dual:3",
                RunnerMessageTypes.Completion,
                new { outcome = "completed" },
                fencingToken: 5),
            occurredAt.AddSeconds(2),
            cancellationToken);
        Assert.True(completion.Receipt?.Applied);

        var afterCompletion = await store.ApplyAsync(
            Message(
                "runner-dual",
                attemptId,
                4,
                "dual:after-completion",
                RunnerMessageTypes.Heartbeat,
                new { observedAt = occurredAt.AddSeconds(4) }),
            occurredAt.AddSeconds(4),
            cancellationToken);
        Assert.Equal(RunnerMessageRejection.AttemptAlreadyCompleted, afterCompletion.Rejection);

        var state = await store.ReadAttemptAsync(attemptId, cancellationToken);
        Assert.NotNull(state);
        // Roteamento, nao posse: o registro aponta para o processo que falou por ultimo.
        Assert.Equal("runner-apos-reinicio", state.RunnerId);
        Assert.Equal(5, state.FencingToken);
        Assert.Equal(3, state.LastSequence);
        Assert.Equal(1, state.HeartbeatCount);
        Assert.Equal(["commit-dual"], state.CheckpointIds);
        Assert.True(state.Completed);
        Assert.Equal(3, state.InboxCount);
        Assert.Equal(3, state.OutboxCount);
        Assert.Equal(3, state.Version);
    }

    private static RunnerMessageEnvelope Message(
        string runnerId,
        string attemptId,
        long sequence,
        string idempotencyKey,
        string type,
        object payload,
        long fencingToken = 0) => new(
            runnerId,
            attemptId,
            sequence,
            idempotencyKey,
            type,
            JsonSerializer.SerializeToElement(payload),
            fencingToken);
}
