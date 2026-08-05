using Harness.Host.Agents;
using Harness.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.RecoveryTests;

/// <summary>
/// Onda 0.6: o estado que explica uma decisão de loop sobrevive ao processo que executa o loop.
///
/// O cenário real que motivou: em 03/08/2026 foram catorze tentativas de zero token em 1h40 —
/// o contador que as teria parado morria a cada restart do Host, e a queda acontecia exatamente
/// quando a fábrica mais tendia a repetir trabalho.
/// </summary>
public sealed class ChiefLoopStateRecoveryTests : IAsyncLifetime
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5L01";
    private const string Card = "01ARZ3NDEKTSV4RRFFQ69G5L02";
    private const string Attempt = "01ARZ3NDEKTSV4RRFFQ69G5L03";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"poseidon-loopstate-{Guid.NewGuid():N}");

    private SqliteWriteDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _dispatcher = await SqliteWriteDispatcher.CreateAsync(Path.Combine(_root, "loop.db"));
        await SqliteMigrationRunner.ApplyAsync(_dispatcher, CancellationToken.None);
    }

    private ChiefLoopDurableState NovoProcesso() =>
        new(new SqliteChiefLoopStateStore(_dispatcher), NullLogger.Instance);

    /// <summary>
    /// O Host "morre" no meio de um backoff (instância nova sobre o mesmo banco) e o backoff
    /// CONTINUA valendo: o card não volta ao despacho imediato.
    /// </summary>
    [Fact]
    public async Task BackoffDeDespachoSobreviveAoReinicio()
    {
        var antes = NovoProcesso();
        await antes.HydrateAsync(Tenant, CancellationToken.None);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(10);
        await antes.SetDispatchBackoffAsync(Tenant, Card, notBefore, CancellationToken.None);

        // REINÍCIO: processo novo, cache vazio, mesma store.
        var depois = NovoProcesso();
        await depois.HydrateAsync(Tenant, CancellationToken.None);

        Assert.True(depois.TryGetDispatchBackoff(Card, out var restaurado));
        Assert.Equal(notBefore, restaurado, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ContagemDeNaoProgressoSobreviveAoReinicioEZeraComProgresso()
    {
        var antes = NovoProcesso();
        await antes.HydrateAsync(Tenant, CancellationToken.None);
        await antes.IncrementNoProgressAsync(Tenant, Card, CancellationToken.None);
        await antes.IncrementNoProgressAsync(Tenant, Card, CancellationToken.None);
        await antes.IncrementNoProgressAsync(Tenant, Card, CancellationToken.None);

        var depois = NovoProcesso();
        await depois.HydrateAsync(Tenant, CancellationToken.None);
        Assert.Equal(3, depois.NoProgressRuns(Card));

        // Progresso real zera — e o zero também sobrevive.
        await depois.ClearNoProgressAsync(Tenant, Card, CancellationToken.None);
        var terceiro = NovoProcesso();
        await terceiro.HydrateAsync(Tenant, CancellationToken.None);
        Assert.Equal(0, terceiro.NoProgressRuns(Card));
    }

    [Fact]
    public async Task BackoffDeRevisaoSobreviveEALimpezaTambem()
    {
        var antes = NovoProcesso();
        await antes.HydrateAsync(Tenant, CancellationToken.None);
        await antes.SetReviewBackoffAsync(
            Tenant, Attempt, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        var depois = NovoProcesso();
        await depois.HydrateAsync(Tenant, CancellationToken.None);
        Assert.True(depois.TryGetReviewBackoff(Attempt, out _));

        await depois.ClearReviewBackoffAsync(Tenant, Attempt, CancellationToken.None);
        var terceiro = NovoProcesso();
        await terceiro.HydrateAsync(Tenant, CancellationToken.None);
        Assert.False(terceiro.TryGetReviewBackoff(Attempt, out _));
    }

    /// <summary>Sem store (instalação antiga), o laço opera como antes — nunca para por falta dela.</summary>
    [Fact]
    public async Task SemStoreOLacoOperaComoAntes()
    {
        var estado = new ChiefLoopDurableState(null, NullLogger.Instance);
        await estado.HydrateAsync(Tenant, CancellationToken.None);
        await estado.SetDispatchBackoffAsync(
            Tenant, Card, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.True(estado.TryGetDispatchBackoff(Card, out _));
    }

    public async Task DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
