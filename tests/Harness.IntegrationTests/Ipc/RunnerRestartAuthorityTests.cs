using System.Globalization;
using System.Text.Json;
using Harness.Persistence.Abstractions.RunnerIpc;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.RunnerIpc;

namespace Harness.IntegrationTests.Ipc;

/// <summary>
/// F13/B5 — autoridade por identidade sobre a persistência real.
///
/// O caso que estes testes defendem é o que o protocolo anterior perdia: um agente que REINICIA
/// ganha identificador de processo novo e, pela regra antiga (<c>runner_owner_conflict</c>), tinha
/// a própria conclusão recusada — com o trabalho feito e o relatório pronto.
/// </summary>
public sealed class RunnerRestartAuthorityTests
{
    /// <summary>Gate da fase: reiniciou, identificador novo, mesmo fencing → aceito.</summary>
    [Fact]
    public async Task RestartedRunnerWithNewProcessIdentifierKeepsAuthorityOverTheAttempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, store) = await CreateStoreAsync(timeout.Token);
        try
        {
            const string attempt = "attempt-reinicio";

            var opened = await store.ApplyAsync(
                Message("runner-original", attempt, sequence: 1, fencing: 7, RunnerMessageTypes.Heartbeat),
                DateTimeOffset.UtcNow, timeout.Token);
            Assert.Equal(RunnerMessageRejection.None, opened.Rejection);

            // O processo morreu e voltou com outro identificador — mesmo fencing, mesma tentativa.
            var afterRestart = await store.ApplyAsync(
                Message("runner-DEPOIS-do-reinicio", attempt, sequence: 2, fencing: 7,
                    RunnerMessageTypes.Completion),
                DateTimeOffset.UtcNow, timeout.Token);

            Assert.Equal(RunnerMessageRejection.None, afterRestart.Rejection);
            var state = await store.ReadAttemptAsync(attempt, timeout.Token);
            Assert.NotNull(state);
            Assert.True(state!.Completed);

            // O identificador acompanha o processo VIVO: ele é roteamento, não posse.
            Assert.Equal("runner-DEPOIS-do-reinicio", state.RunnerId);
            Assert.Equal(7, state.FencingToken);
        }
        finally
        {
            await store.DisposeAsync();
            Cleanup(root);
        }
    }

    /// <summary>Gate da fase: resultado tardio de tentativa superada é rejeitado.</summary>
    [Fact]
    public async Task LateMessageFromASupersededFencingIsRejected()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, store) = await CreateStoreAsync(timeout.Token);
        try
        {
            const string attempt = "attempt-superado";
            await store.ApplyAsync(
                Message("runner-novo", attempt, sequence: 1, fencing: 9, RunnerMessageTypes.Heartbeat),
                DateTimeOffset.UtcNow, timeout.Token);

            var late = await store.ApplyAsync(
                Message("runner-antigo", attempt, sequence: 2, fencing: 4, RunnerMessageTypes.Completion),
                DateTimeOffset.UtcNow, timeout.Token);

            Assert.Equal(RunnerMessageRejection.StaleFencingToken, late.Rejection);
            var state = await store.ReadAttemptAsync(attempt, timeout.Token);
            Assert.False(state!.Completed);
        }
        finally
        {
            await store.DisposeAsync();
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RunnersWithoutFencingKeepWorkingUnchanged()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (root, store) = await CreateStoreAsync(timeout.Token);
        try
        {
            const string attempt = "attempt-legado";

            // Runner anterior ao B5: fencing ausente (0). Recusá-lo seria pior que aceitar.
            await store.ApplyAsync(
                Message("runner-a", attempt, sequence: 1, fencing: 0, RunnerMessageTypes.Heartbeat),
                DateTimeOffset.UtcNow, timeout.Token);
            var second = await store.ApplyAsync(
                Message("runner-b", attempt, sequence: 2, fencing: 0, RunnerMessageTypes.Completion),
                DateTimeOffset.UtcNow, timeout.Token);

            Assert.Equal(RunnerMessageRejection.None, second.Rejection);
            Assert.True((await store.ReadAttemptAsync(attempt, timeout.Token))!.Completed);
        }
        finally
        {
            await store.DisposeAsync();
            Cleanup(root);
        }
    }

    private static RunnerMessageEnvelope Message(
        string runnerId, string attemptId, long sequence, long fencing, string type) =>
        new(
            runnerId,
            attemptId,
            sequence,
            $"chave:{attemptId}:{sequence.ToString(CultureInfo.InvariantCulture)}",
            type,
            JsonDocument.Parse("""{"nota":"carga estruturada"}""").RootElement,
            fencing);

    private static async Task<(string Root, SqliteRunnerMessageStore Store)> CreateStoreAsync(
        CancellationToken token)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            "f13-runner-authority",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var store = new SqliteRunnerMessageStore(Path.Combine(root, "runner.db"));
        await store.ReadAttemptAsync("bootstrap", token);
        return (root, store);
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Artefato em disco não é resultado.
        }
    }
}
