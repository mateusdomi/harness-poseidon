using Harness.Host.WorkBoard;
using Harness.Host.Workers;
using Harness.Persistence.Abstractions.Messaging;
using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;
using Harness.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 0A1 — regressão permanente de BR-001 e BR-004.
///
/// Cada teste MATA a materialização em uma aresta real (depois do primeiro card, no meio dos cards,
/// antes do marker, depois do marker) e exige convergência: exatamente o conjunto previsto, zero
/// duplicatas, marker apenas com o conjunto completo e nenhum planejamento invisivelmente
/// incompleto. O que antes acontecia — marker antes dos cards, retry saindo cedo pelo marker e
/// materialização em memória depois do commit do turno — falha aqui.
/// </summary>
public sealed class PlanMaterializationDurabilityTests
{
    [Fact]
    public async Task MaterializesExactlyOnceAndIsIdempotentAcrossRetriesAndConcurrency()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        var first = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        Assert.Equal(PlanMaterializationResult.Materialized, first.Result);
        Assert.Equal(expected, first.CreatedCards);
        await fixture.AssertConvergedAsync(expected, timeout.Token);

        // Retry e entrega duplicada do evento: NÃO recriam nada e NÃO reabrem o compromisso.
        var replay = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        Assert.Equal(PlanMaterializationResult.AlreadyComplete, replay.Result);
        var duplicated = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-b", timeout.Token);
        Assert.Equal(PlanMaterializationResult.AlreadyComplete, duplicated.Result);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task TwoConcurrentConsumersConvergeToTheSameCardsWithoutDuplicates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        var outcomes = await Task.WhenAll(
            Task.Run(() => fixture.Service().RunAsync(
                PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token), timeout.Token),
            Task.Run(() => fixture.Service().RunAsync(
                PlanFixture.Tenant, fixture.DemandId, "owner-b", timeout.Token), timeout.Token));

        // Um conclui; o outro NÃO pode declarar conclusão sem o fencing — e nenhum dos dois pode
        // criar um segundo conjunto de cards.
        Assert.Single(outcomes, outcome => outcome.Result == PlanMaterializationResult.Materialized);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Theory]
    [InlineData(PlanMaterializationStage.BeforeConsume)]
    [InlineData(PlanMaterializationStage.AfterFirstCard)]
    [InlineData(PlanMaterializationStage.BetweenCards)]
    [InlineData(PlanMaterializationStage.AfterCards)]
    [InlineData(PlanMaterializationStage.AfterDependencies)]
    [InlineData(PlanMaterializationStage.BeforeMarker)]
    [InlineData(PlanMaterializationStage.AfterMarker)]
    [InlineData(PlanMaterializationStage.BeforeAcknowledgement)]
    public async Task ACrashAtAnyEdgeConvergesOnTheNextAttemptWithoutDuplicatingCards(
        PlanMaterializationStage stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        var crashing = fixture.Service(new CrashingFaultInjector(stage));
        await Assert.ThrowsAsync<PlanMaterializationCrash>(
            () => crashing.RunAsync(PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token));

        // Depois do acknowledgement o trabalho JÁ está feito e confirmado: a queda seguinte não
        // pode desfazer nem reabrir nada — só a confirmação do evento se perde, e a reentrega é
        // inofensiva. Nas demais arestas, a queda deixa a falha visível e retentável.
        var afterCrash = await fixture.Jobs.GetAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        Assert.NotNull(afterCrash);
        var settledBeforeCrash = stage == PlanMaterializationStage.BeforeAcknowledgement;
        Assert.Equal(
            settledBeforeCrash ? PlanMaterializationStatus.Completed : PlanMaterializationStatus.Failed,
            afterCrash!.Status);

        // O plano SÓ pode estar carimbado se o conjunto estiver completo. Antes, o marker vinha
        // antes dos cards e uma queda no meio deixava a lacuna permanente.
        var plan = await fixture.Plans.GetByDemandAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        if (plan?.MaterializedAt is not null)
        {
            var partial = await fixture.Board.ListPlanCardsAsync(PlanFixture.Tenant, plan.Id, timeout.Token);
            Assert.Equal(expected, partial.Count);
        }

        var recovered = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-b", timeout.Token);
        Assert.Equal(
            settledBeforeCrash
                ? PlanMaterializationResult.AlreadyComplete
                : PlanMaterializationResult.Materialized,
            recovered.Result);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task AProcessThatDiedMidFlightIsRecoveredOnceItsLeaseGoesStale()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        // Uma queda de PROCESSO não executa catch nenhum: o compromisso fica `processing` sob um
        // dono que não existe mais. Nada além do lease vencido pode liberá-lo — do contrário dois
        // donos trabalhariam ao mesmo tempo.
        await fixture.AbandonInProcessingAsync(TimeSpan.FromHours(1), timeout.Token);
        var blockedWhileFresh = await fixture.Jobs.TryBeginAsync(
            new PlanMaterializationBeginCommand(
                PlanFixture.Tenant, fixture.DemandId, "owner-late",
                new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(6)),
            timeout.Token);
        Assert.Null(blockedWhileFresh);

        var result = await fixture.Reconciler().RunOnceAsync(timeout.Token);
        Assert.Equal(1, result.Recovered);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task TheReconcilerRecoversACommandThatWasNeverConsumed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        // O turno commitou o compromisso e o processo morreu antes de consumir o comando.
        var pending = await fixture.Jobs.GetAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        Assert.Equal(PlanMaterializationStatus.Pending, pending!.Status);

        var result = await fixture.Reconciler().RunOnceAsync(timeout.Token);
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Recovered);
        await fixture.AssertConvergedAsync(expected, timeout.Token);

        // Uma segunda passada não altera nada: reconciliar é idempotente.
        var again = await fixture.Reconciler().RunOnceAsync(timeout.Token);
        Assert.Equal(0, again.Recovered);
        Assert.Equal(0, again.Repaired);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task TheReconcilerRepairsAMarkedPlanWhoseCardDisappeared()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);
        await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        await fixture.AssertConvergedAsync(expected, timeout.Token);

        // Marker existente + card ausente: exatamente o estado que BR-001 deixava permanente.
        var plan = await fixture.Plans.GetByDemandAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        await fixture.DeleteOnePlanCardAsync(plan!.Id, timeout.Token);
        Assert.Equal(
            expected - 1,
            (await fixture.Board.ListPlanCardsAsync(PlanFixture.Tenant, plan.Id, timeout.Token)).Count);

        var result = await fixture.Reconciler().RunOnceAsync(timeout.Token);
        Assert.Equal(1, result.Repaired);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task ARetryDoesNotTrustTheMarkerAndRebuildsTheMissingSlice()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);
        await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        var plan = await fixture.Plans.GetByDemandAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        await fixture.DeleteOnePlanCardAsync(plan!.Id, timeout.Token);

        // O registro diz `completed` e o plano tem marker — e mesmo assim a tentativa mede o board
        // e reconstrói. Sair cedo aqui era o defeito.
        var outcome = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-c", timeout.Token);
        Assert.Equal(PlanMaterializationResult.Materialized, outcome.Result);
        Assert.Equal(1, outcome.CreatedCards);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
    }

    [Fact]
    public async Task CardsCreatedBeforeThePlanKeyExistedAreAdoptedInsteadOfDuplicated()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);
        await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        var plan = await fixture.Plans.GetByDemandAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);

        // Simula o mundo anterior à migration: os cards existem, mas sem a chave lógica do plano.
        await fixture.StripPlanKeyAsync(plan!.Id, timeout.Token);
        Assert.Empty(await fixture.Board.ListPlanCardsAsync(PlanFixture.Tenant, plan.Id, timeout.Token));

        var outcome = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-d", timeout.Token);
        Assert.Equal(PlanMaterializationResult.Materialized, outcome.Result);
        Assert.Equal(0, outcome.CreatedCards);
        await fixture.AssertConvergedAsync(expected, timeout.Token);
        Assert.Equal(expected, await fixture.CountDemandCardsAsync(timeout.Token));
    }

    [Fact]
    public async Task AnUnresolvableDependencyFailsVisiblyAndIsNotRetriedForever()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);

        // Um plano que declara dependência de uma fatia inexistente prenderia o card no backlog
        // para sempre — trabalho invisível. O bloco exige falha explícita, não silêncio.
        await fixture.CorruptPlanDependencyAsync(timeout.Token);
        var outcome = await fixture.Service().RunAsync(
            PlanFixture.Tenant, fixture.DemandId, "owner-a", timeout.Token);
        Assert.Equal(PlanMaterializationResult.Failed, outcome.Result);
        Assert.Equal("plan_dependency_unresolved", outcome.ErrorCode);

        var record = await fixture.Jobs.GetAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        Assert.Equal(PlanMaterializationStatus.Failed, record!.Status);
        Assert.Equal("plan_dependency_unresolved", record.LastError);

        // O plano NÃO pode aparecer materializado.
        var plan = await fixture.Plans.GetByDemandAsync(PlanFixture.Tenant, fixture.DemandId, timeout.Token);
        Assert.Null(plan!.MaterializedAt);

        // O reconciliador reconhece a falha terminal e não fica repetindo o que não tem conserto.
        var reconciled = await fixture.Reconciler().RunOnceAsync(timeout.Token);
        Assert.Equal(1, reconciled.Blocked);
        Assert.Equal(0, reconciled.Recovered);
    }

    [Fact]
    public async Task TheOutboxCommandIsRoutedToTheConsumerAndSurvivesADuplicateDelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var fixture = await PlanFixture.StartAsync(timeout.Token);
        var expected = await fixture.ExpectedSliceCountAsync(timeout.Token);

        // O caminho REAL: o comando gravado na transação do turno é consumido pelo dispatcher da
        // outbox, com o lease e o fencing dela. Entregar duas vezes é o comportamento normal de um
        // at-least-once — e não pode produzir um segundo conjunto de cards.
        await fixture.EnqueueMaterializationCommandAsync(timeout.Token);
        await fixture.EnqueueMaterializationCommandAsync(timeout.Token);
        var recorder = new RecordingSink();
        var dispatched = await fixture.OutboxDispatcher(recorder).DispatchAvailableAsync(timeout.Token);

        // O lote inclui os dois comandos e os eventos de tempo real que a materialização gerou.
        Assert.True(dispatched >= 2, $"The dispatcher handled only {dispatched} messages.");
        await fixture.AssertConvergedAsync(expected, timeout.Token);

        // O comando interno NÃO é evento de tempo real: ele nunca chega ao sink do navegador.
        Assert.DoesNotContain(recorder.EventTypes, type => type == "plan.materializationRequested");
        Assert.Contains(recorder.EventTypes, type => type == "task.created");
    }

    private sealed class RecordingSink : IOutboxMessageSink
    {
        private readonly List<string> _eventTypes = [];

        public IReadOnlyList<string> EventTypes => _eventTypes;

        public Task DispatchAsync(OutboxLease message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            _eventTypes.Add(message.EventType);
            return Task.CompletedTask;
        }
    }

    /// <summary>A queda simulada. Só existe em teste; o Host registra o injetor no-op.</summary>
    private sealed class PlanMaterializationCrash(PlanMaterializationStage stage)
        : Exception($"Simulated crash at {stage}.");

    private sealed class CrashingFaultInjector(PlanMaterializationStage stage)
        : IPlanMaterializationFaultInjector
    {
        public Task SignalAsync(
            PlanMaterializationStage signalled, CancellationToken cancellationToken = default) =>
            signalled == stage
                ? throw new PlanMaterializationCrash(stage)
                : Task.CompletedTask;
    }

    private sealed class IncrementingClock : IClock
    {
        private DateTimeOffset _now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _now = _now.AddMilliseconds(250);
    }

    private sealed class PlanFixture : IAsyncDisposable
    {
        public const string Tenant = FoundationTransactionBehavior.TenantId;
        private const string Project = FoundationTransactionBehavior.ProjectId;

        private readonly string _root;
        private readonly SqliteWriteDispatcher _dispatcher;
        private readonly IncrementingClock _clock = new();
        private readonly ILocalProfileStore _profiles;
        private readonly PlanMaterializationOptions _options =
            new(TimeSpan.FromMinutes(5), MaximumAttempts: 5);

        private PlanFixture(string root, SqliteWriteDispatcher dispatcher, string demandId)
        {
            _root = root;
            _dispatcher = dispatcher;
            DemandId = demandId;
            Board = new SqliteWorkBoardStore(dispatcher);
            Plans = new SqliteDemandPlanStore(dispatcher);
            Jobs = new SqlitePlanMaterializationStore(dispatcher);
            _profiles = new SqliteLocalProfileStore(dispatcher);
        }

        public string DemandId { get; }

        public SqliteWorkBoardStore Board { get; }

        public SqliteDemandPlanStore Plans { get; }

        public SqlitePlanMaterializationStore Jobs { get; }

        public static async Task<PlanFixture> StartAsync(CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"plan0a1-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "plan.db"), cancellationToken);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, cancellationToken);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), cancellationToken);
            var now = new DateTimeOffset(2026, 7, 31, 11, 0, 0, TimeSpan.Zero);
            var profiles = new SqliteLocalProfileStore(dispatcher);
            await profiles.CreateAsync(
                new LocalProfileCreateCommand(
                    Tenant, "Tenant", UlidValue.New(now).ToString(), "Mateus", null, null,
                    "pt-BR", now, JoinExistingTenant: true),
                cancellationToken);
            var board = new SqliteWorkBoardStore(dispatcher);
            var solicitationId = UlidValue.New(now.AddMilliseconds(1)).ToString();
            var demandId = UlidValue.New(now.AddMilliseconds(2)).ToString();
            var profileId = (await profiles.ListAsync(cancellationToken))[0].Id;
            await board.CreateSolicitationAsync(
                new BoardSolicitationCreateCommand(
                    Tenant, solicitationId, Project, profileId, "request",
                    "CAT-09 Publicar catálogo",
                    "Investigar a incerteza técnica; implementar o backend do serviço e a tela React; " +
                    "integrar com o gateway usando a credencial externa de homologação.",
                    null, now.AddMilliseconds(3)),
                cancellationToken);
            await board.CreateDemandAsync(
                new BoardDemandCreateCommand(
                    Tenant, demandId, Project, solicitationId, solicitationId, profileId,
                    "CAT-09 Publicar catálogo",
                    "Investigar a incerteza técnica; implementar o backend do serviço e a tela React; " +
                    "integrar com o gateway usando a credencial externa de homologação.",
                    "high", now.AddMilliseconds(4)),
                cancellationToken);
            var fixture = new PlanFixture(root, dispatcher, demandId);

            // O que a transação do turno grava: o compromisso durável com a intenção declarada.
            await fixture.Jobs.RequestAsync(
                new PlanMaterializationRequestCommand(
                    Tenant, demandId, Project, null,
                    new PlanMaterializationRequest(["O catálogo responde 200."]),
                    now.AddMilliseconds(5)),
                cancellationToken);
            return fixture;
        }

        public PlanMaterializationService Service(IPlanMaterializationFaultInjector? faults = null) =>
            new(
                Jobs,
                Board,
                Plans,
                new DemandPlanMaterializer(Board, Plans),
                _profiles,
                new SqliteChiefLoopGuardStore(_dispatcher),
                _clock,
                _options,
                faults ?? NullPlanMaterializationFaultInjector.Instance,
                NullLogger<PlanMaterializationService>.Instance);

        public PlanMaterializationReconciliationBackgroundService Reconciler() =>
            new(
                Jobs,
                Service(),
                _clock,
                new PlanMaterializationReconciliationOptions(
                    TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 250, "reconciler"),
                _options,
                NullLogger<PlanMaterializationReconciliationBackgroundService>.Instance);

        /// <summary>Quantas fatias o planner prevê para esta demanda, sem materializar nada.</summary>
        public async Task<int> ExpectedSliceCountAsync(CancellationToken cancellationToken)
        {
            var demand = await Board.GetDemandAsync(Tenant, DemandId, cancellationToken);
            var saved = await new DemandPlanMaterializer(Board, Plans).EnsurePlanAsync(
                Tenant, demand!, ["O catálogo responde 200."], null, null,
                new DateTimeOffset(2026, 7, 31, 11, 30, 0, TimeSpan.Zero), cancellationToken);
            return saved.Plan.Cards.Count;
        }

        /// <summary>As invariantes do bloco, verificadas contra o BANCO, não contra o retorno.</summary>
        public async Task AssertConvergedAsync(int expected, CancellationToken cancellationToken)
        {
            var plan = await Plans.GetByDemandAsync(Tenant, DemandId, cancellationToken);
            Assert.NotNull(plan);
            Assert.NotNull(plan!.MaterializedAt);
            Assert.Equal(expected, plan.Cards.Count);

            var cards = await Board.ListPlanCardsAsync(Tenant, plan.Id, cancellationToken);
            Assert.Equal(expected, cards.Count);
            Assert.Equal(expected, cards.Select(card => card.SliceKey).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected, cards.Select(card => card.TaskId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(cards, card => Assert.Equal("backlog", card.State));

            // Dependências: toda dependência declarada resolve para uma fatia materializada.
            var keys = cards.Select(card => card.SliceKey).ToHashSet(StringComparer.Ordinal);
            foreach (var dependency in plan.Cards.SelectMany(card => card.Dependencies))
            {
                Assert.Contains(dependency, keys);
            }

            // Nenhum card órfão da demanda além do conjunto previsto.
            Assert.Equal(expected, await CountDemandCardsAsync(cancellationToken));

            var record = await Jobs.GetAsync(Tenant, DemandId, cancellationToken);
            Assert.Equal(PlanMaterializationStatus.Completed, record!.Status);
            Assert.Equal(plan.Id, record.PlanId);
            Assert.Equal(expected, record.ExpectedCards);
            Assert.Equal(expected, record.MaterializedCards);
            Assert.NotNull(record.CompletedAt);
            Assert.Null(record.LastError);
        }

        public Task<int> CountDemandCardsAsync(CancellationToken cancellationToken) =>
            _dispatcher.ExecuteAsync(async (connection, token) =>
            {
                await using var query = connection.CreateCommand();
                query.CommandText =
                    "SELECT COUNT(*) FROM work_tasks WHERE tenant_id=$tenant AND demand_id=$demand;";
                query.Parameters.AddWithValue("$tenant", Tenant);
                query.Parameters.AddWithValue("$demand", DemandId);
                return Convert.ToInt32(
                    await query.ExecuteScalarAsync(token),
                    System.Globalization.CultureInfo.InvariantCulture);
            }, cancellationToken);

        public async Task DeleteOnePlanCardAsync(string planId, CancellationToken cancellationToken) =>
            await
            _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    DELETE FROM instruction_versions WHERE task_id IN (
                        SELECT id FROM work_tasks WHERE tenant_id=$tenant AND plan_id=$plan
                        ORDER BY plan_slice_key DESC LIMIT 1);
                    DELETE FROM work_tasks WHERE id IN (
                        SELECT id FROM work_tasks WHERE tenant_id=$tenant AND plan_id=$plan
                        ORDER BY plan_slice_key DESC LIMIT 1);
                    """;
                command.Parameters.AddWithValue("$tenant", Tenant);
                command.Parameters.AddWithValue("$plan", planId);
                await command.ExecuteNonQueryAsync(token);
                return null;
            }, cancellationToken);

        public async Task StripPlanKeyAsync(string planId, CancellationToken cancellationToken) =>
            await
            _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE work_tasks SET plan_id=NULL,plan_slice_key=NULL " +
                    "WHERE tenant_id=$tenant AND plan_id=$plan;";
                command.Parameters.AddWithValue("$tenant", Tenant);
                command.Parameters.AddWithValue("$plan", planId);
                await command.ExecuteNonQueryAsync(token);
                return null;
            }, cancellationToken);

        /// <summary>Grava na outbox o mesmo comando que a transação do turno grava.</summary>
        public async Task EnqueueMaterializationCommandAsync(CancellationToken cancellationToken) =>
            await _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
            {
                var at = _clock.UtcNow;
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO outbox_messages
                        (id,tenant_id,event_type,payload_json,occurred_at,available_at)
                    VALUES ($id,$tenant,'plan.materializationRequested',$payload,$at,$at);
                    """;
                command.Parameters.AddWithValue("$id", UlidValue.New(at).ToString());
                command.Parameters.AddWithValue("$tenant", Tenant);
                command.Parameters.AddWithValue(
                    "$payload",
                    $$"""{"tenantId":"{{Tenant}}","projectId":"{{Project}}","demandId":"{{DemandId}}","turnId":null}""");
                command.Parameters.AddWithValue(
                    "$at", at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(token);
                return null;
            }, cancellationToken);

        public OutboxDispatcherBackgroundService OutboxDispatcher(IOutboxMessageSink realtime) =>
            new(
                new SqliteOutboxStore(_dispatcher),
                new PlanMaterializationOutboxSink(
                    Service(),
                    realtime,
                    OutboxOptions,
                    NullLogger<PlanMaterializationOutboxSink>.Instance),
                _clock,
                OutboxOptions);

        private static readonly OutboxDispatcherOptions OutboxOptions = new(
            "test-outbox-owner",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(50),
            100,
            new OutboxRetryPolicy(5, TimeSpan.FromSeconds(1), 2m, TimeSpan.FromMinutes(1)));

        /// <summary>Deixa o compromisso preso em `processing` sob um dono que morreu.</summary>
        public async Task AbandonInProcessingAsync(TimeSpan age, CancellationToken cancellationToken) =>
            await _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE demand_materializations " +
                    "SET status='processing',owner_id='owner-dead',attempt_count=1,updated_at=$at " +
                    "WHERE tenant_id=$tenant AND demand_id=$demand;";
                command.Parameters.AddWithValue("$tenant", Tenant);
                command.Parameters.AddWithValue("$demand", DemandId);
                command.Parameters.AddWithValue(
                    "$at",
                    (new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero) - age)
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(token);
                return null;
            }, cancellationToken);

        /// <summary>Injeta no plano persistido uma dependência que nenhuma fatia satisfaz.</summary>
        public async Task CorruptPlanDependencyAsync(CancellationToken cancellationToken)
        {
            _ = await ExpectedSliceCountAsync(cancellationToken);
            await _dispatcher.ExecuteAsync<object?>(async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE demand_plans SET cards_json=json_set(cards_json,'$[0].dependencies', " +
                    "json_array('CAT-09/T99')) WHERE tenant_id=$tenant AND demand_id=$demand;";
                command.Parameters.AddWithValue("$tenant", Tenant);
                command.Parameters.AddWithValue("$demand", DemandId);
                await command.ExecuteNonQueryAsync(token);
                return null;
            }, cancellationToken);
        }

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
