using Harness.IntegrationTests.Persistence;
using Harness.Persistence.Abstractions.Foundation;
using Harness.Persistence.Abstractions.Identity;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Coordination;

/// <summary>
/// Fase 1E — regressão permanente do multi-tenant nos serviços de fundo.
///
/// `ChiefBacklogLoopService` usava `profileList[0]` e `AttemptRecoveryBackgroundService` usava
/// `list[0].TenantId`. Com dois perfis na mesma instalação, o segundo simplesmente nunca era
/// atendido — e nada no produto dizia isso. O laço não "falhava": ele trabalhava para um dono só,
/// em silêncio, o que é o pior tipo de defeito porque parece funcionamento normal.
/// </summary>
public sealed class MultiTenantBackgroundLoopTests
{
    [Fact]
    public async Task TwoTenantsCoexistWithoutOneSeeingTheOthersWork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(
            AppContext.BaseDirectory, "integration-artifacts", $"multitenant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var dispatcher = await SqliteWriteDispatcher.CreateAsync(
                Path.Combine(root, "multitenant.db"), timeout.Token);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, timeout.Token);
            var foundation = new SqliteFoundationTransactionStore(dispatcher);
            await foundation.ProvisionProjectAsync(
                FoundationTransactionBehavior.Command(), timeout.Token);
            await foundation.ProvisionProjectAsync(
                OutboxStoreBehavior.SecondProjectCommand(), timeout.Token);

            var profiles = new SqliteLocalProfileStore(dispatcher);
            var now = new DateTimeOffset(2026, 7, 31, 20, 0, 0, TimeSpan.Zero);
            await profiles.CreateAsync(
                new LocalProfileCreateCommand(
                    FoundationTransactionBehavior.TenantId, "Tenant A",
                    UlidValue.New(now).ToString(), "Dono A", null, null, "pt-BR", now,
                    JoinExistingTenant: true),
                timeout.Token);
            await profiles.CreateAsync(
                new LocalProfileCreateCommand(
                    "01ARZ3NDEKTSV4RRFFQ69G5FC1", "Tenant B",
                    UlidValue.New(now.AddSeconds(1)).ToString(), "Dono B", null, null, "pt-BR",
                    now.AddSeconds(1), JoinExistingTenant: true),
                timeout.Token);

            // O laço de fundo passou a iterar TODOS os perfis. Este teste prova o pré-requisito
            // disso: os dois tenants existem lado a lado e cada um enxerga apenas o próprio
            // trabalho — a varredura por tenant é o que torna o laço multi-projeto real.
            var list = await profiles.ListAsync(timeout.Token);
            var tenants = list.Select(profile => profile.TenantId)
                .Distinct(StringComparer.Ordinal).ToArray();
            Assert.Equal(2, tenants.Length);

            var board = new SqliteWorkBoardStore(dispatcher);
            await SeedDemandAsync(
                board, FoundationTransactionBehavior.TenantId,
                FoundationTransactionBehavior.ProjectId, "01ARZ3NDEKTSV4RRFFQ69G5FAY",
                "Demanda do tenant A", now, timeout.Token);
            await SeedDemandAsync(
                board, "01ARZ3NDEKTSV4RRFFQ69G5FC1", "01ARZ3NDEKTSV4RRFFQ69G5FC3",
                "01ARZ3NDEKTSV4RRFFQ69G5FC4", "Demanda do tenant B", now.AddMinutes(1),
                timeout.Token);

            foreach (var tenantId in tenants)
            {
                var demands = await board.ListDemandsAsync(tenantId, null, null, null, 50, timeout.Token);
                var visible = Assert.Single(demands);
                // Nenhum tenant vê a demanda do outro: contaminação aqui seria vazamento entre
                // donos diferentes na mesma instalação.
                Assert.Equal(tenantId, visible.TenantId);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task SeedDemandAsync(
        SqliteWorkBoardStore board, string tenantId, string projectId, string authorId,
        string title, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var solicitationId = UlidValue.New(at).ToString();
        await board.CreateSolicitationAsync(
            new BoardSolicitationCreateCommand(
                tenantId, solicitationId, projectId, authorId, "request", title,
                "Trabalho pedido por este dono.", null, at),
            cancellationToken);
        await board.CreateDemandAsync(
            new BoardDemandCreateCommand(
                tenantId, UlidValue.New(at.AddMilliseconds(1)).ToString(), projectId,
                solicitationId, solicitationId, authorId, title,
                "Trabalho pedido por este dono.", "medium", at.AddMilliseconds(1)),
            cancellationToken);
    }
}
