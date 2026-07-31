using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Agents;
using Harness.Persistence.Abstractions.Conversations;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 0A2 — regressão permanente de BR-005.
///
/// O lease do turno durava dois minutos e NADA o renovava: o batimento publicava um evento de tela
/// e não tocava no lease. Uma inferência mais longa que isso fazia o turno vivo parecer abandonado,
/// outro worker o readquiria, o modelo era chamado de novo e o dono pagava duas vezes pela mesma
/// pergunta — com dois efeitos externos para uma resposta só.
///
/// A recuperação de turnos REALMENTE abandonados continua valendo: é o que separa a correção de um
/// bloqueio permanente.
/// </summary>
public sealed class ChiefTurnLeaseRenewalTests
{
    private const string Tenant = FoundationTransactionBehavior.TenantId;
    private const string Organization = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string Author = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FE1";

    [Fact]
    public async Task ARenewedTurnIsNotReacquiredWhileAnAbandonedOneStillIs()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"lease0a2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "lease.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var store = new SqliteConversationStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
            var lease = await AcquireTurnAsync(dispatcher, store, now, "worker-a", TimeSpan.FromMinutes(2), timeout.Token);

            // A inferência passa do lease original, mas o batimento renova a cada 30 segundos.
            for (var minute = 1; minute <= 6; minute++)
            {
                var renewal = await store.TryRenewAsync(
                    new ChiefTurnRenewCommand(
                        lease, now.AddMinutes(minute), TimeSpan.FromMinutes(2)),
                    timeout.Token);
                Assert.True(renewal.Renewed);

                // Em NENHUM instante o turno vivo pode ser readquirido — era exatamente aqui que o
                // segundo worker entrava e o custo dobrava.
                Assert.Null(await store.AcquireNextAsync(
                    "worker-b", now.AddMinutes(minute).AddSeconds(1), TimeSpan.FromMinutes(2),
                    timeout.Token));
            }

            // O dono continua podendo concluir o turno depois de seis minutos de trabalho.
            var completedAt = now.AddMinutes(6).AddSeconds(30);
            await store.CompleteAsync(
                new ChiefTurnCompleteCommand(
                    lease,
                    new MessageRecord(
                        Tenant, Project, UlidValue.New(completedAt).ToString(),
                        lease.Turn.ConversationId, "chief", null, lease.ChiefAgentId,
                        "Resposta após uma inferência longa.", null, completedAt),
                    ["Resposta após uma inferência longa."],
                    "session-lease",
                    """{"phase":"lease"}""",
                    completedAt),
                timeout.Token);
            Assert.Equal(
                "completed",
                (await store.GetAsync(Tenant, lease.Turn.TurnId, timeout.Token))!.State);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AnAbandonedTurnIsStillRecoveredAndTheOldOwnerLosesTheFencing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"lease0a2-lost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "lease.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            await new SqliteFoundationTransactionStore(dispatcher).ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            var store = new SqliteConversationStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero);
            var abandoned = await AcquireTurnAsync(
                dispatcher, store, now, "worker-a", TimeSpan.FromMinutes(2), timeout.Token);

            // O processo morreu: ninguém renovou. Depois do vencimento, o trabalho volta a ser
            // recuperável — a correção do BR-005 não pode transformar queda em turno preso.
            var recovered = await store.AcquireNextAsync(
                "worker-b", now.AddMinutes(5), TimeSpan.FromMinutes(2), timeout.Token);
            Assert.NotNull(recovered);
            Assert.Equal(abandoned.Turn.TurnId, recovered!.Turn.TurnId);
            Assert.True(recovered.FencingToken > abandoned.FencingToken);

            // O dono antigo não renova mais nada: perdeu o fencing.
            Assert.False((await store.TryRenewAsync(
                new ChiefTurnRenewCommand(abandoned, now.AddMinutes(6), TimeSpan.FromMinutes(2)),
                timeout.Token)).Renewed);

            // E também não conclui: o store recusa a escrita tardia.
            var lateAt = now.AddMinutes(6).AddSeconds(1);
            await Assert.ThrowsAsync<ChiefTurnConflictException>(() => store.CompleteAsync(
                new ChiefTurnCompleteCommand(
                    abandoned,
                    new MessageRecord(
                        Tenant, Project, UlidValue.New(lateAt).ToString(),
                        abandoned.Turn.ConversationId, "chief", null, abandoned.ChiefAgentId,
                        "Resposta tardia do dono antigo.", null, lateAt),
                    ["Resposta tardia do dono antigo."],
                    "session-late",
                    """{"phase":"late"}""",
                    lateAt),
                timeout.Token));

            // O novo dono renova normalmente.
            Assert.True((await store.TryRenewAsync(
                new ChiefTurnRenewCommand(recovered, now.AddMinutes(6), TimeSpan.FromMinutes(2)),
                timeout.Token)).Renewed);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ChiefTurnLease> AcquireTurnAsync(
        SqliteWriteDispatcher dispatcher,
        SqliteConversationStore store,
        DateTimeOffset now,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var conversationId = UlidValue.New(now).ToString();
        var chiefAgentId = UlidValue.New(now.AddTicks(1)).ToString();
        // Criar o projeto pelo store é o que provisiona o AGENTE chefe: sem ele, o estado do chefe
        // não tem a quem apontar.
        await new SqliteProjectStore(dispatcher).CreateAsync(
            new Harness.Persistence.Abstractions.Projects.ProjectCreateCommand(
                Tenant,
                new Harness.Persistence.Abstractions.Projects.ProjectRecord(
                    Tenant, Project, Organization, "Lease", "LEASE", "Turnos longos",
                    "active", "medium", null, "local", "main", [],
                    new Harness.Persistence.Abstractions.Projects.ProjectBrandRecord(
                        null, null, null, null),
                    [Author], 1, chiefAgentId, "manual", now, now, 0),
                now),
            cancellationToken);
        await store.CreateConversationAsync(
            new ConversationCreateCommand(
                new ConversationRecord(
                    Tenant, conversationId, Project, "Lease", "active", Author, now, null, 1),
                now),
            cancellationToken);
        var turnId = UlidValue.New(now.AddMilliseconds(1)).ToString();
        var messageId = UlidValue.New(now.AddMilliseconds(2)).ToString();
        await store.EnqueueAsync(
            new ChiefTurnEnqueueCommand(
                Tenant, Project, conversationId, turnId, chiefAgentId,
                new MessageRecord(
                    Tenant, Project, messageId, conversationId, "user", Author, null,
                    "Pergunta que exige uma inferência longa.", null, now.AddMilliseconds(2)),
                $"chief-turn:{turnId}", now.AddMilliseconds(3)),
            cancellationToken);
        var lease = await store.AcquireNextAsync(owner, now, leaseDuration, cancellationToken);
        Assert.NotNull(lease);
        return lease!;
    }
}
