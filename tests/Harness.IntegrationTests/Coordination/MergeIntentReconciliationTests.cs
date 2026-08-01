using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 0C1/0C2 — regressão permanente de BR-003.
///
/// O merge Git acontecia ANTES do commit factual, sem intenção registrada e coordenado por um
/// `SemaphoreSlim` em memória. Uma falha de banco entre as duas coisas deixava o código integrado e
/// o card em `approved` para sempre — divergência silenciosa. E o semáforo protegia um processo,
/// não o repositório: dois Hosts colidiam no mesmo merge.
/// </summary>
public sealed class MergeIntentReconciliationTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Project = FoundationTransactionBehavior.ProjectId;
    private const string Repository = "/repos/poseidon";

    [Fact]
    public async Task OnlyOneMergeIsActivePerRepositoryAndTheIntentIsIdempotentPerAttempt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;

        var first = await fixture.RequestAsync("attempt-a", now, timeout.Token);
        // Idempotente por TENTATIVA: um retry reusa a mesma intenção em vez de abrir outra e
        // arriscar dois merges para o mesmo trabalho.
        var replay = await fixture.RequestAsync("attempt-a", now.AddSeconds(1), timeout.Token);
        Assert.Equal(first.MergeIntentId, replay.MergeIntentId);

        var second = await fixture.RequestAsync("attempt-b", now.AddSeconds(2), timeout.Token);
        Assert.NotEqual(first.MergeIntentId, second.MergeIntentId);

        var claimed = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(Tenant, first.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.NotNull(claimed);

        // Segundo Host, MESMO repositório: recusado pelo banco, não por um semáforo de processo.
        Assert.Null(await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, second.MergeIntentId, "host-b", now.AddSeconds(3), TimeSpan.FromMinutes(10)),
            timeout.Token));

        // Concluído o primeiro, o repositório é liberado para o próximo.
        Assert.True(await fixture.Store.TryRecordMergedAsync(
            new MergeIntentResultCommand(
                Tenant, first.MergeIntentId, "host-a", claimed!.FencingToken,
                new string('a', 40), now.AddMinutes(1)),
            timeout.Token));
        Assert.NotNull(await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, second.MergeIntentId, "host-b", now.AddMinutes(2), TimeSpan.FromMinutes(10)),
            timeout.Token));
    }

    [Fact]
    public async Task AnOwnerThatLostTheFencingCannotConfirmTheResult()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;
        var intent = await fixture.RequestAsync("attempt-c", now, timeout.Token);
        var first = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(Tenant, intent.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(1)),
            timeout.Token);

        // O dono morreu; o lease venceu e outro Host assumiu o MESMO intent.
        var second = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-b", now.AddMinutes(5), TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.NotNull(second);
        Assert.True(second!.FencingToken > first!.FencingToken);

        // O dono antigo NÃO confirma resultado: um merge tardio dele não pode reescrever o estado.
        Assert.False(await fixture.Store.TryRecordMergedAsync(
            new MergeIntentResultCommand(
                Tenant, intent.MergeIntentId, "host-a", first.FencingToken,
                new string('b', 40), now.AddMinutes(6)),
            timeout.Token));
        Assert.True(await fixture.Store.TryRecordMergedAsync(
            new MergeIntentResultCommand(
                Tenant, intent.MergeIntentId, "host-b", second.FencingToken,
                new string('c', 40), now.AddMinutes(7)),
            timeout.Token));
    }

    [Fact]
    public async Task AMergeWithoutTheFactualCommitStaysVisibleUntilItIsSettled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;
        var intent = await fixture.RequestAsync("attempt-d", now, timeout.Token);
        var claimed = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(Tenant, intent.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(10)),
            timeout.Token);
        await fixture.Store.TryRecordMergedAsync(
            new MergeIntentResultCommand(
                Tenant, intent.MergeIntentId, "host-a", claimed!.FencingToken,
                new string('d', 40), now.AddMinutes(1)),
            timeout.Token);

        // Este é o estado exato do BR-003: o código está integrado e o banco ainda não sabe.
        // Ele PRECISA continuar visível para o reconciliador — antes, sumia.
        var unsettled = await fixture.Store.ListUnsettledAsync(50, timeout.Token);
        Assert.Contains(unsettled, record => record.MergeIntentId == intent.MergeIntentId);

        Assert.True(await fixture.Store.TrySettleBoardAsync(
            Tenant, intent.MergeIntentId, now.AddMinutes(2), timeout.Token));

        // Conciliado, sai da fila — e repetir a conciliação não reabre nada.
        var after = await fixture.Store.ListUnsettledAsync(50, timeout.Token);
        Assert.DoesNotContain(after, record => record.MergeIntentId == intent.MergeIntentId);
        var settled = await fixture.Store.GetAsync(Tenant, intent.MergeIntentId, timeout.Token);
        Assert.True(settled!.BoardSettled);
        Assert.Equal(new string('d', 40), settled.ResultSha);
    }

    [Fact]
    public async Task AnAbortedMergeReleasesTheRepositoryWithoutClaimingIntegration()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;
        var intent = await fixture.RequestAsync("attempt-e", now, timeout.Token);
        var claimed = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(Tenant, intent.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(10)),
            timeout.Token);

        // Conflito Git: aborta SEM afirmar integração e sem tocar no card.
        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-a", claimed!.FencingToken,
                "GitMergeConflict", Aborted: true, now.AddMinutes(1)),
            timeout.Token));
        var aborted = await fixture.Store.GetAsync(Tenant, intent.MergeIntentId, timeout.Token);
        Assert.Equal(MergeIntentState.Aborted, aborted!.State);
        Assert.Null(aborted.ResultSha);
        Assert.False(aborted.BoardSettled);

        // O repositório fica livre para o próximo card imediatamente.
        var other = await fixture.RequestAsync("attempt-f", now.AddMinutes(2), timeout.Token);
        Assert.NotNull(await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, other.MergeIntentId, "host-b", now.AddMinutes(3), TimeSpan.FromMinutes(10)),
            timeout.Token));
    }

    [Fact]
    public async Task TheLegacyManagedRootArgumentFailureIsReopenedExactlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;
        var intent = await fixture.RequestAsync("attempt-root", now, timeout.Token);
        var first = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-a", first!.FencingToken,
                "ArgumentException", Aborted: true, now.AddSeconds(1)),
            timeout.Token));

        var reopened = await fixture.RequestAsync("attempt-root", now.AddSeconds(2), timeout.Token);
        Assert.Equal(MergeIntentState.Pending, reopened.State);
        var second = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-b", now.AddSeconds(3), TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.Equal(2, second!.FencingToken);

        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-b", second.FencingToken,
                "ArgumentException", Aborted: true, now.AddSeconds(4)),
            timeout.Token));
        var terminal = await fixture.RequestAsync("attempt-root", now.AddSeconds(5), timeout.Token);
        Assert.Equal(MergeIntentState.Aborted, terminal.State);
    }

    [Fact]
    public async Task AProvenSupersededDeliveryCanReopenTheLastLegacyAbortOnlyOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await Fixture.StartAsync(timeout.Token);
        var now = fixture.Now;
        var intent = await fixture.RequestAsync("attempt-superseded", now, timeout.Token);
        var first = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-a", now, TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-a", first!.FencingToken,
                "ArgumentException", Aborted: true, now.AddSeconds(1)),
            timeout.Token));
        _ = await fixture.RequestAsync("attempt-superseded", now.AddSeconds(2), timeout.Token);
        var second = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-b", now.AddSeconds(3), TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-b", second!.FencingToken,
                "InvalidOperationException", Aborted: true, now.AddSeconds(4)),
            timeout.Token));

        var reopened = await fixture.Store.TryReopenSupersededAsync(
            new MergeIntentSupersessionCommand(
                Tenant, intent.MergeIntentId, second.FencingToken,
                "document:approved-newer-version", now.AddSeconds(5)),
            timeout.Token);
        Assert.Equal(MergeIntentState.Pending, reopened?.State);
        Assert.Equal("superseded:document:approved-newer-version", reopened?.LastError);
        var final = await fixture.Store.TryBeginAsync(
            new MergeIntentBeginCommand(
                Tenant, intent.MergeIntentId, "host-c", now.AddSeconds(6), TimeSpan.FromMinutes(10)),
            timeout.Token);
        Assert.Equal(3, final?.FencingToken);
        Assert.True(await fixture.Store.TryFailAsync(
            new MergeIntentFailCommand(
                Tenant, intent.MergeIntentId, "host-c", final!.FencingToken,
                "InvalidOperationException", Aborted: true, now.AddSeconds(7)),
            timeout.Token));
        Assert.Null(await fixture.Store.TryReopenSupersededAsync(
            new MergeIntentSupersessionCommand(
                Tenant, intent.MergeIntentId, final.FencingToken,
                "document:approved-newer-version", now.AddSeconds(8)),
            timeout.Token));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteDispatcher _dispatcher;

        private Fixture(string root, SqliteWriteDispatcher dispatcher)
        {
            _root = root;
            _dispatcher = dispatcher;
            Store = new SqliteMergeIntentStore(dispatcher);
        }

        public DateTimeOffset Now { get; } = new(2026, 7, 31, 16, 0, 0, TimeSpan.Zero);

        public SqliteMergeIntentStore Store { get; }

        public static async Task<Fixture> StartAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"merge0c-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "merge.db"), cancellationToken);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), cancellationToken);
            return new Fixture(root, dispatcher);
        }

        public Task<MergeIntentRecord> RequestAsync(
            string attemptId, DateTimeOffset at, CancellationToken cancellationToken) =>
            Store.RequestAsync(
                new MergeIntentRequestCommand(
                    Tenant, UlidValue.New(at).ToString(), Repository, Project,
                    $"card-{attemptId}", attemptId, $"task/{attemptId}", "main", null, null, at),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _dispatcher.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
