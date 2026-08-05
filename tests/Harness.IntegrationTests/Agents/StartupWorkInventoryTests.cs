using Harness.Host.Agents;
using Harness.Persistence.Abstractions.Projects;
using Harness.Persistence.Abstractions.WorkChain;
using Harness.Persistence.Sqlite;
using Harness.SharedKernel.Identifiers;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// A pergunta que não tinha resposta antes de 2026-08-04: <b>se eu ligar o Poseidon agora, o que
/// ele vai tentar executar?</b>
///
/// Ela passou a valer dinheiro. Durante o desenvolvimento nasceram dezenas de projetos de teste,
/// vários parados no meio do playbook; um Host que subisse e os encontrasse elegíveis gastaria em
/// trabalho velho a cota que o projeto real precisa — e o sintoma apareceria depois, disfarçado de
/// "o provedor acabou".
///
/// O inventário usa o MESMO predicado do laço de despacho, não uma cópia. Estes testes existem para
/// garantir que ele continue respondendo pela regra real, e não por uma regra paralela que um dia
/// diverge em silêncio.
/// </summary>
public sealed class StartupWorkInventoryTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5W00";
    private const string Org = "01ARZ3NDEKTSV4RRFFQ69G5W01";

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "integration-artifacts", $"startup-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;
    private SqliteProjectStore _projects = null!;
    private SqliteWorkBoardStore _board = null!;
    private StartupWorkInventory _inventory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "startup.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
        await SeedTenantAsync();

        _projects = new SqliteProjectStore(_dispatcher);
        _board = new SqliteWorkBoardStore(_dispatcher);
        _inventory = new StartupWorkInventory(
            new SqliteLocalProfileStore(_dispatcher), _projects, _board);
    }

    [Fact]
    public async Task InstalacaoSemProjetoNaoTemNadaParaDespachar()
    {
        var snapshot = await _inventory.InspectAsync(CancellationToken.None);

        Assert.True(snapshot.IsEmpty, snapshot.Summary());
        Assert.Equal(0, snapshot.RunnableProjects);
        Assert.Equal(0, snapshot.RunnableCards);
    }

    /// <summary>
    /// Projeto ATIVO com card pronto é trabalho — e precisa aparecer. Um inventário que devolvesse
    /// zero para tudo seria inútil e, pior, tranquilizador.
    /// </summary>
    [Fact]
    public async Task ProjetoAtivoComCardProntoApareceNoInventario()
    {
        var projectId = await CriarProjetoAsync("ativo", "active");
        await CriarCardProntoAsync(projectId);

        var snapshot = await _inventory.InspectAsync(CancellationToken.None);

        Assert.False(snapshot.IsEmpty);
        Assert.Equal(1, snapshot.RunnableProjects);
        Assert.Equal(1, snapshot.RunnableCards);
    }

    /// <summary>
    /// Projeto PAUSADO some do inventário: é decisão explícita do dono, e o laço de despacho o pula
    /// antes de qualquer revisão, integração ou condução de fase.
    /// </summary>
    [Fact]
    public async Task ProjetoPausadoComCardProntoNaoContaComoTrabalho()
    {
        var projectId = await CriarProjetoAsync("pausado", "paused");
        await CriarCardProntoAsync(projectId);

        var snapshot = await _inventory.InspectAsync(CancellationToken.None);

        Assert.True(snapshot.IsEmpty, snapshot.Summary());
        Assert.Equal(0, snapshot.RunnableProjects);
    }

    /// <summary>
    /// A REGRESSÃO FUNDAMENTAL desta operação — o cenário do Bloco L, inteiro:
    ///
    /// projeto antigo com card incompleto → projeto removido → Host "reinicia" (instâncias novas
    /// sobre o mesmo banco) → o projeto não reaparece, o card não é despachável, e o inventário
    /// continua zero.
    ///
    /// Remover é mais forte do que pausar porque o store filtra <c>deleted_at IS NULL</c> na
    /// listagem: o projeto deixa de existir para quem procura trabalho, e nenhuma referência órfã o
    /// traz de volta — o card continua no banco e nunca é alcançado, porque o caminho até ele passa
    /// pela listagem de projetos.
    /// </summary>
    [Fact]
    public async Task ProjetoRemovidoNaoRessuscitaNoReinicioNemDespachaCard()
    {
        var projectId = await CriarProjetoAsync("antigo", "active");
        await CriarCardProntoAsync(projectId);

        var antes = await _inventory.InspectAsync(CancellationToken.None);
        Assert.Equal(1, antes.RunnableCards);

        var record = await _projects.GetAsync(Tenant, projectId, CancellationToken.None);
        Assert.NotNull(record);
        var removido = await _projects.DeleteAsync(
            Tenant, projectId, record.Version, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(ProjectMutationStatus.Applied, removido.Status);

        // "Reinício": stores e inventário NOVOS sobre o mesmo estado durável. Se algo tivesse
        // ficado em memória, é aqui que a diferença apareceria.
        var reiniciado = new StartupWorkInventory(
            new SqliteLocalProfileStore(_dispatcher),
            new SqliteProjectStore(_dispatcher),
            new SqliteWorkBoardStore(_dispatcher));
        var depois = await reiniciado.InspectAsync(CancellationToken.None);

        Assert.True(depois.IsEmpty, depois.Summary());
        Assert.Equal(0, depois.RunnableProjects);
        Assert.Equal(0, depois.RunnableCards);

        // O projeto não volta para a listagem, e o card continua gravado — histórico preservado,
        // execução impossível.
        Assert.DoesNotContain(
            await _projects.ListAsync(Tenant, null, 50, CancellationToken.None),
            item => item.Id == projectId);
        var page = await _board.PageTasksAsync(
            Tenant,
            new BoardTaskPageQuery(projectId, null, null, "ready", null, null, "active", null, 0, 50),
            CancellationToken.None);
        Assert.NotEmpty(page.Items);
    }

    // ---------- fixtures ----------

    private async Task SeedTenantAsync()
    {
        await _dispatcher.ExecuteAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO tenants (id, name, version, created_at) " +
                "VALUES ($tenant, 'preflight', 1, $now);" +
                "INSERT INTO local_users (id, tenant_id, display_name, version, created_at) " +
                "VALUES ($profile, $tenant, 'preflight', 1, $now);" +
                "INSERT INTO organizations (tenant_id, id, name, version, created_at) " +
                "VALUES ($tenant, $org, 'preflight', 1, $now);";
            command.Parameters.AddWithValue("$tenant", Tenant);
            command.Parameters.AddWithValue("$profile", "01ARZ3NDEKTSV4RRFFQ69G5W02");
            command.Parameters.AddWithValue("$org", Org);
            command.Parameters.AddWithValue("$now", "2026-08-04T12:00:00.0000000+00:00");
            return await command.ExecuteNonQueryAsync(token);
        }, CancellationToken.None);
    }

    /// <summary>
    /// O projeto entra pelo SCHEMA, não pelo caso de uso de criação: o que este arquivo mede é o
    /// predicado de elegibilidade, e passar pela criação completa traria seed de workflow, ledger e
    /// outbox que não têm relação com a pergunta. A REMOÇÃO, essa sim, usa o domínio — é ela que a
    /// regressão precisa exercitar de verdade.
    /// </summary>
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
            command.Parameters.AddWithValue("$key", nome.ToUpperInvariant());
            command.Parameters.AddWithValue("$state", estado);
            command.Parameters.AddWithValue("$now", "2026-08-04T12:00:00.0000000+00:00");
            return await command.ExecuteNonQueryAsync(token);
        }, CancellationToken.None);
        return id;
    }

    private async Task CriarCardProntoAsync(string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var taskId = UlidValue.New(now).ToString();
        _ = await _board.CreateTaskAsync(
            new BoardTaskCreateCommand(
                Tenant, taskId, projectId, null,
                UlidValue.New(now.AddMilliseconds(1)).ToString(),
                UlidValue.New(now.AddMilliseconds(2)).ToString(),
                "01ARZ3NDEKTSV4RRFFQ69G5W02", "card antigo incompleto", "low", null, null,
                UlidValue.New(now.AddMilliseconds(3)).ToString(),
                "Implementar alguma coisa que ficou pela metade.", now, "5-Desenvolvimento",
                "agent_task"),
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
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Sobra de artefato temporário não é falha de teste.
        }
    }
}
