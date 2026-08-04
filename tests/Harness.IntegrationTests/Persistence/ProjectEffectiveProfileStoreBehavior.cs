using Harness.Modules.Workflows.Product;
using Harness.Persistence.Abstractions.Product;
using Harness.Persistence.Sqlite;

namespace Harness.IntegrationTests.Persistence;

/// <summary>
/// O perfil efetivo é um SNAPSHOT DE DECISÃO. Quando o baseline global mudar, um card executado
/// hoje precisa continuar auditável sob a decisão que valia hoje — por isso o histórico é
/// append-only e a versão é explícita.
/// </summary>
public sealed class ProjectEffectiveProfileStoreBehavior
{
    private const string Tenant = "tenant-perfil";
    private const string Project = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public async Task PrimeiraResolucaoNasceNaVersaoUmEViraAVigente()
    {
        await using var fixture = await Fixture.CreateAsync();
        var profile = Web();

        var result = await fixture.Store.SaveAsync(Command(profile, "arquiteto"));

        Assert.True(result.Created);
        Assert.Equal(1, result.Profile.Version);
        Assert.Equal("active", result.Profile.Status);
        Assert.Equal(profile.Fingerprint(), result.Profile.Fingerprint);
        Assert.Equal("1.0.0", result.Profile.BaselineVersion);
        Assert.Equal("Web", result.Profile.Modality);

        var current = await fixture.Store.GetCurrentAsync(Tenant, Project);
        Assert.Equal(1, current!.Version);
    }

    [Fact]
    public async Task ResolverDuasVezesAMesmaCoisaNaoCriaVersaoNova()
    {
        // Decisão que não mudou não é decisão nova; versionar o idêntico faria "mudou de versão"
        // perder o significado.
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync(Command(Web(), "arquiteto"));

        var again = await fixture.Store.SaveAsync(Command(Web(), "arquiteto"));

        Assert.False(again.Created);
        Assert.Equal(1, again.Profile.Version);
        Assert.Single(await fixture.Store.ListAsync(Tenant, Project));
    }

    [Fact]
    public async Task OverrideCriaVersaoNovaEPreservaAAnterior()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync(Command(Web(), "arquiteto"));

        var angular = EffectiveProfileResolver.Resolve(new EffectiveProfileInputs(
            Project, "Sistema de empréstimos.",
            [new ProfileDirective(
                ProfileAuthority.ApprovedDecision,
                EffectiveProfileResolver.AreaFrontendFramework,
                "Angular", "Padronização corporativa", "ADR-0042")]));
        var second = await fixture.Store.SaveAsync(Command(angular, "arquiteto"));

        Assert.True(second.Created);
        Assert.Equal(2, second.Profile.Version);

        var history = await fixture.Store.ListAsync(Tenant, Project);
        Assert.Equal(2, history.Count);
        Assert.Equal("active", history[0].Status);
        Assert.Equal("superseded", history[1].Status);

        // A pergunta que o histórico existe para responder: sob qual decisão o card antigo rodou.
        var v1 = await fixture.Store.GetVersionAsync(Tenant, Project, 1);
        var restored = ProjectEffectiveProfile.FromJson(v1!.ProfileJson);
        Assert.Equal("React", restored!.Frontend.Framework);
        Assert.Empty(restored.Overrides);

        var v2 = ProjectEffectiveProfile.FromJson(second.Profile.ProfileJson);
        Assert.Equal("Angular", v2!.Frontend.Framework);
        Assert.Equal("ADR-0042", Assert.Single(v2.Overrides).AdrId);
    }

    [Fact]
    public async Task ProjetoSemPerfilDevolveNuloEmVezDeInventarUm()
    {
        await using var fixture = await Fixture.CreateAsync();

        Assert.Null(await fixture.Store.GetCurrentAsync(Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FZZ"));
        Assert.Empty(await fixture.Store.ListAsync(Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FZZ"));
    }

    [Fact]
    public async Task PerfilPersistidoSobreviveAReaberturaDoBanco()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Store.SaveAsync(Command(Web(), "arquiteto"));

        await using var reopened = await fixture.ReopenAsync();
        var current = await reopened.Store.GetCurrentAsync(Tenant, Project);

        Assert.NotNull(current);
        Assert.Equal(Web().Fingerprint(), current.Fingerprint);
    }

    private static ProjectEffectiveProfile Web() => EffectiveProfileResolver.Resolve(
        new EffectiveProfileInputs(Project, "Crie um sistema simples de empréstimos.", []));

    private static ProjectEffectiveProfileSaveCommand Command(
        ProjectEffectiveProfile profile, string resolvedBy) =>
        new(Tenant, Project, profile.Fingerprint(), profile.BaselineVersion,
            profile.Modality.ToString(), profile.ToJson(),
            new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero), resolvedBy);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath;

        private Fixture(SqliteWriteDispatcher dispatcher, string databasePath)
        {
            Dispatcher = dispatcher;
            _databasePath = databasePath;
            Store = new SqliteProjectEffectiveProfileStore(dispatcher);
        }

        public SqliteWriteDispatcher Dispatcher { get; }

        public SqliteProjectEffectiveProfileStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(
                AppContext.BaseDirectory, "integration-artifacts", $"profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "harness.db");
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(databasePath, CancellationToken.None);
            await SqliteMigrationRunner.ApplyAsync(dispatcher, CancellationToken.None);
            return new Fixture(dispatcher, databasePath);
        }

        public async Task<Fixture> ReopenAsync()
        {
            var dispatcher = await SqliteWriteDispatcher.CreateAsync(_databasePath, CancellationToken.None);
            return new Fixture(dispatcher, _databasePath);
        }

        public async ValueTask DisposeAsync() => await Dispatcher.DisposeAsync();
    }
}
