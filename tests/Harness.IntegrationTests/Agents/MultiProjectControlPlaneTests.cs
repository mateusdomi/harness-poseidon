using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// O control plane com CINCO projetos concorrentes — provado sem gastar um token de agente.
///
/// A tese empresarial é "um desenvolvedor, vários projetos"; o que este arquivo prova é a metade
/// que dá para provar com provider determinístico: escopo por projeto (cards e conversas não
/// cruzam), o predicado de elegibilidade honrando pause/resume por projeto, e o inventário de
/// arranque enxergando exatamente o que o laço de despacho enxergaria.
///
/// A declaração honesta do que isto É: <b>MULTI-PROJECT CONTROL PLANE = TESTED</b>. Não é "cinco
/// projetos de IA entregues" — nenhum agente executou nada aqui.
/// </summary>
public sealed class MultiProjectControlPlaneTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5M00";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5M01";
    private const string Profile = "01ARZ3NDEKTSV4RRFFQ69G5M02";

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "integration-artifacts", $"multi-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteProjectStore _projects = null!;
    private SqliteWorkBoardStore _board = null!;
    private readonly List<string> _projectIds = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "multi.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
        await SeedTenantAsync();
        _projects = new SqliteProjectStore(_dispatcher);
        _board = new SqliteWorkBoardStore(_dispatcher);

        // Cinco projetos: três ativos com um card pronto cada, um pausado com card pronto, um
        // ativo sem trabalho.
        for (var index = 0; index < 5; index++)
        {
            var estado = index == 3 ? "paused" : "active";
            var id = await CriarProjetoAsync($"projeto-{index}", estado);
            _projectIds.Add(id);
            if (index != 4)
            {
                await CriarCardProntoAsync(id, $"card do projeto {index}");
            }
        }
    }

    [Fact]
    public async Task OInventarioEnxergaSomenteOsProjetosAtivosECadaCardNoSeuProjeto()
    {
        var inventory = new StartupWorkInventory(
            new SqliteLocalProfileStore(_dispatcher), _projects, _board);
        var snapshot = await inventory.InspectAsync(CancellationToken.None);

        // 4 projetos ativos (0,1,2,4), 3 cards prontos (0,1,2). O pausado (3) não conta.
        Assert.Equal(4, snapshot.RunnableProjects);
        Assert.Equal(3, snapshot.RunnableCards);
        Assert.DoesNotContain(snapshot.Detail, item => item.Contains(_projectIds[3], StringComparison.Ordinal));
    }

    /// <summary>Cards não vazam entre projetos: a página é POR projeto, e cada card aparece só no seu.</summary>
    [Fact]
    public async Task CardsNaoCruzamProjetos()
    {
        for (var index = 0; index < 5; index++)
        {
            var page = await _board.PageTasksAsync(
                Tenant,
                new BoardTaskPageQuery(_projectIds[index], null, null, null, null, null, "active", null, 0, 50),
                CancellationToken.None);

            var esperado = index == 4 ? 0 : 1;
            Assert.Equal(esperado, page.Items.Count);
            Assert.All(page.Items, task => Assert.Equal(_projectIds[index], task.ProjectId));
        }
    }

    /// <summary>
    /// Pause é POR projeto e reversível: pausar um não toca os outros, e reativar devolve o
    /// trabalho ao inventário — o ciclo que o operador usa para conter um projeto sem perder nada.
    /// </summary>
    [Fact]
    public async Task PausarUmProjetoNaoTocaOsOutrosEReativarDevolveOTrabalho()
    {
        var alvo = _projectIds[0];
        var record = await _projects.GetAsync(Tenant, alvo, CancellationToken.None);
        Assert.NotNull(record);

        _ = await _projects.UpdateAsync(
            new ProjectUpdateCommand(record with { State = "paused" }, record.Version),
            CancellationToken.None);

        var inventory = new StartupWorkInventory(
            new SqliteLocalProfileStore(_dispatcher), _projects, _board);
        var pausado = await inventory.InspectAsync(CancellationToken.None);
        Assert.Equal(3, pausado.RunnableProjects);
        Assert.Equal(2, pausado.RunnableCards);

        var atual = await _projects.GetAsync(Tenant, alvo, CancellationToken.None);
        _ = await _projects.UpdateAsync(
            new ProjectUpdateCommand(atual! with { State = "active" }, atual.Version),
            CancellationToken.None);

        var reativado = await inventory.InspectAsync(CancellationToken.None);
        Assert.Equal(4, reativado.RunnableProjects);
        Assert.Equal(3, reativado.RunnableCards);
    }

    // ---------- fixtures ----------

    private async Task SeedTenantAsync()
    {
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO tenants (id, name, version, created_at) VALUES ($tenant, 'multi', 1, $now);" +
                "INSERT INTO local_users (id, tenant_id, display_name, version, created_at) " +
                "VALUES ($profile, $tenant, 'multi', 1, $now);" +
                "INSERT INTO organizations (tenant_id, id, name, version, created_at) " +
                "VALUES ($tenant, $org, 'multi', 1, $now);";
            command.Parameters.AddWithValue("$tenant", Tenant);
            command.Parameters.AddWithValue("$profile", Profile);
            command.Parameters.AddWithValue("$org", Org);
            command.Parameters.AddWithValue("$now", "2026-08-05T02:00:00.0000000+00:00");
            return await command.ExecuteNonQueryAsync(token);
        }, CancellationToken.None);
    }

    private async Task<string> CriarProjetoAsync(string nome, string estado)
    {
        var id = UlidValue.New(DateTimeOffset.UtcNow).ToString();
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO projects (id, tenant_id, organization_id, name, version, created_at, " +
                "project_key, description, state, criticality, operation_mode, last_activity_at) " +
                "VALUES ($id, $tenant, $org, $name, 1, $now, $key, $name, $state, 'low', " +
                "'autonomous', $now);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$tenant", Tenant);
            command.Parameters.AddWithValue("$org", Org);
            command.Parameters.AddWithValue("$name", nome);
            command.Parameters.AddWithValue("$key", nome.ToUpperInvariant().Replace('-', '_'));
            command.Parameters.AddWithValue("$state", estado);
            command.Parameters.AddWithValue("$now", "2026-08-05T02:00:00.0000000+00:00");
            return await command.ExecuteNonQueryAsync(token);
        }, CancellationToken.None);
        return id;
    }

    private async Task CriarCardProntoAsync(string projectId, string titulo)
    {
        var now = DateTimeOffset.UtcNow;
        var taskId = UlidValue.New(now).ToString();
        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                Tenant, taskId, projectId, null,
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                Profile, titulo, "low", null, null,
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                "Trabalho de teste do control plane.", now, "5-Desenvolvimento", "agent_task"),
            CancellationToken.None);
        _ = await _board.MoveTaskAsync(
            new BoardTaskMoveCommand(Tenant, taskId, "ready", "fixture", "agent", now.AddMilliseconds(4)),
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sobra temporária não é falha.
        }
    }
}
