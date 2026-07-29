using System.Globalization;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.Coordination;
using Harness.Persistence.Sqlite;

namespace Harness.RecoveryTests;

/// <summary>
/// F12/B4+B9 — o que estas provas defendem é a sobrevivência ao REINÍCIO, porque é exatamente aí
/// que os dois defeitos voltam: um circuito guardado em memória reabre sozinho quando o processo
/// sobe, e um contador de laço em memória zera e deixa a Bruna recomeçar o giro do começo.
/// </summary>
public sealed class CoordinationLoopGuardRecoveryTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OpenCircuitSurvivesRestartAndStillNeedsReplanToReopen()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = CreateRoot("f12-circuit-restart");
        var databasePath = Path.Combine(root, "circuit.db");
        try
        {
            await using (var dispatcher = await OpenAsync(databasePath, seed: true, timeout.Token))
            {
                var store = new SqliteCardCircuitBreakerStore(dispatcher);
                for (var index = 0; index < CardCircuitBreakerPolicy.ConsecutiveFailureThreshold; index++)
                {
                    await store.RecordFailureAsync(
                        Tenant, Project, "card-1", Now.AddMinutes(index),
                        CardCircuitBreakerPolicy.ConsecutiveFailureThreshold, "agent.run_failed",
                        timeout.Token);
                }

                Assert.True((await store.GetAsync(Tenant, "card-1", timeout.Token))!.IsOpen);
            }

            // Reinício do processo: outro dispatcher, outra instância de store, o mesmo arquivo.
            await using (var dispatcher = await OpenAsync(databasePath, seed: false, timeout.Token))
            {
                var store = new SqliteCardCircuitBreakerStore(dispatcher);
                var recovered = await store.GetAsync(Tenant, "card-1", timeout.Token);
                Assert.NotNull(recovered);
                Assert.True(recovered!.IsOpen);
                Assert.Equal(3, recovered.ConsecutiveFailures);

                // Subir o processo não é replanejar: o card continua fora do despacho.
                Assert.Equal(
                    ["card-1"],
                    (await store.ListOpenAsync(Tenant, Project, timeout.Token))
                        .Select(record => record.TaskId));

                await store.ReplanAsync(
                    Tenant, Project, "card-1", Now.AddHours(2), "escopo cortado", timeout.Token);
                Assert.Empty(await store.ListOpenAsync(Tenant, Project, timeout.Token));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task CausalCycleInterruptionKeepsItsEvidenceAcrossRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = CreateRoot("f12-cycle-restart");
        var databasePath = Path.Combine(root, "guards.db");
        try
        {
            var demand = ChiefLoopGuardPolicy.DemandKey("demand-1");
            var plan = ChiefLoopGuardPolicy.PlanKey("plan-1");
            var card = ChiefLoopGuardPolicy.CardKey("card-1");

            await using (var dispatcher = await OpenAsync(databasePath, seed: true, timeout.Token))
            {
                var store = new SqliteChiefLoopGuardStore(dispatcher);
                await store.AddCausalEdgeAsync(
                    Edge("01ARZ3NDEKTSV4RRFFQ69G5FB1", demand, plan, "plan.materialized"),
                    timeout.Token);
                await store.AddCausalEdgeAsync(
                    Edge("01ARZ3NDEKTSV4RRFFQ69G5FB2", plan, card, "card.created"),
                    timeout.Token);

                // O card quer regenerar a demanda que o originou: A gerou B que regenerou A.
                var edges = await store.ListCausalEdgesAsync(Tenant, Project, timeout.Token);
                var verdict = ChiefLoopGuardPolicy.Evaluate(
                    new ChiefTurnTrigger("demand-1", Now, SelfTriggered: true, "plan-1", card),
                    new ChiefLoopGuardFacts(CausalEdges:
                        [.. edges.Select(edge => new CausalEdge(edge.CauseKey, edge.EffectKey))]));

                Assert.False(verdict.Allowed);
                Assert.Equal(ChiefLoopGuardPolicy.ReasonCausalCycle, verdict.ReasonCode);
                Assert.NotNull(verdict.Cycle);

                await store.RecordInterruptionAsync(
                    new ChiefLoopInterruptionRecord(
                        Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FB3", Project, "demand-1", "plan-1",
                        verdict.ReasonCode, verdict.Detail, verdict.Cycle!.Path, Now),
                    timeout.Token);
            }

            await using (var dispatcher = await OpenAsync(databasePath, seed: false, timeout.Token))
            {
                var store = new SqliteChiefLoopGuardStore(dispatcher);
                var interruptions = await store.ListInterruptionsAsync(Tenant, "demand-1", timeout.Token);

                // A evidência precisa sobreviver: interrupção sem motivo registrado é travamento.
                var interruption = Assert.Single(interruptions);
                Assert.Equal(ChiefLoopGuardPolicy.ReasonCausalCycle, interruption.ReasonCode);
                Assert.Equal([card, demand, plan, card], interruption.CyclePath);

                // E o grafo continua fechando o ciclo depois do reinício.
                var edges = await store.ListCausalEdgesAsync(Tenant, Project, timeout.Token);
                Assert.NotNull(CausalCycleDetector.DetectClosingCycle(
                    [.. edges.Select(edge => new CausalEdge(edge.CauseKey, edge.EffectKey))],
                    card,
                    demand));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SelfTriggeredCeilingCountsAcrossRestartInsteadOfStartingOver()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = CreateRoot("f12-ceiling-restart");
        var databasePath = Path.Combine(root, "guards.db");
        var limits = new ChiefLoopGuardLimits(MaxSelfTriggeredTurnsPerDemand: 3);
        try
        {
            await using (var dispatcher = await OpenAsync(databasePath, seed: true, timeout.Token))
            {
                var store = new SqliteChiefLoopGuardStore(dispatcher);
                for (var index = 0; index < 3; index++)
                {
                    await store.RecordSelfTriggeredTurnAsync(
                        new ChiefSelfTriggeredTurnRecord(
                            Tenant, $"01ARZ3NDEKTSV4RRFFQ69G5F{index:D2}", Project, "demand-1",
                            "plan-1", null, null, Now.AddMinutes(index)),
                        timeout.Token);
                }
            }

            await using (var dispatcher = await OpenAsync(databasePath, seed: false, timeout.Token))
            {
                var store = new SqliteChiefLoopGuardStore(dispatcher);
                var count = await store.CountSelfTriggeredTurnsAsync(Tenant, "demand-1", timeout.Token);
                Assert.Equal(3, count);

                var verdict = ChiefLoopGuardPolicy.Evaluate(
                    new ChiefTurnTrigger("demand-1", Now.AddMinutes(10), SelfTriggered: true),
                    new ChiefLoopGuardFacts(SelfTriggeredTurnsForDemand: count),
                    limits);

                Assert.False(verdict.Allowed);
                Assert.Equal(ChiefLoopGuardPolicy.ReasonDemandCeiling, verdict.ReasonCode);

                // O dono pedindo continua passando: a guarda é contra a autoalimentação da Bruna.
                Assert.True(ChiefLoopGuardPolicy.Evaluate(
                    new ChiefTurnTrigger("demand-1", Now.AddMinutes(10), SelfTriggered: false),
                    new ChiefLoopGuardFacts(SelfTriggeredTurnsForDemand: count),
                    limits).Allowed);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Gate da fase: nenhuma combinação de prazo estourado mata um agente vivo. O reinício do Host
    /// não muda isso — o que decide é heartbeat e processo, não o relógio da espera.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(600)]
    public void LiveAgentIsNeverReclaimedHoweverLateTheDeadlineIs(int minutesPastDeadline)
    {
        var facts = new AgentWaitFacts(
            Now,
            LeaseExpiresAt: Now.AddMinutes(-minutesPastDeadline),
            WaitDeadline: Now.AddMinutes(-minutesPastDeadline),
            LastHeartbeatAt: Now.AddSeconds(-15),
            ProcessAlive: true);

        var verdict = AgentWaitPolicy.Decide(facts);

        Assert.True(AgentWaitPolicy.IsAgentAlive(facts));
        Assert.False(verdict.MayReclaim);
        Assert.Equal(AgentWaitAction.Checkpoint, verdict.Action);
        Assert.False(verdict.EndsWait);
    }

    private static ChiefCausalEdgeRecord Edge(
        string id, string cause, string effect, string relation) =>
        new(Tenant, id, Project, cause, effect, relation, Now);

    private static string CreateRoot(string name)
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "poc-artifacts",
            name,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<SqliteWriteDispatcher> OpenAsync(
        string databasePath, bool seed, CancellationToken token)
    {
        var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, token);
        await SqliteMigrationRunner.ApplyAsync(dispatcher, token);
        if (!seed)
        {
            return dispatcher;
        }

        await dispatcher.ExecuteAsync(
            async (connection, inner) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     INSERT INTO tenants (id, name, created_at)
                     VALUES ('{Tenant}', 'Tenant', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO organizations (id, tenant_id, name, created_at)
                     VALUES ('{Organization}', '{Tenant}', 'Organization', '2026-07-29T12:00:00.0000000+00:00');
                     INSERT INTO projects (id, tenant_id, organization_id, name, created_at)
                     VALUES ('{Project}', '{Tenant}', '{Organization}', 'Project', '2026-07-29T12:00:00.0000000+00:00');
                     """;
                await command.ExecuteNonQueryAsync(inner);
                return 0;
            },
            token);
        return dispatcher;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Artefato em disco não é resultado: falha ao limpar não reprova a suíte.
        }
    }
}
