using Harness.Host.Product;

namespace Harness.ConcurrencyTests;

/// <summary>
/// A trava que impede a máquina de morrer no Golden Run.
///
/// O que se está protegendo é concreto: uma avaliação de entrega Web dispara dois <c>dotnet
/// build</c>, uma suíte, um build de frontend, duas subidas de API, um servidor de interface e um
/// navegador. Duas avaliações simultâneas não somam — competem. Esta máquina já travou assim.
///
/// O caso que mais importa é o último: <b>licença presa por exceção</b>. Uma licença que não volta
/// para o contador para TODA verificação seguinte, e o sintoma — fila que não anda — é
/// indistinguível de uma fila legítima. Seria o defeito mais caro de diagnosticar de todos os que
/// esta trava pode introduzir.
/// </summary>
public sealed class HeavyWorkPermitTests
{
    [Fact]
    public async Task ASegundaOperacaoPesadaEsperaAPrimeiraDevolverALicenca()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 1);

        using var primeira = await permit.AcquireAsync(CancellationToken.None);
        Assert.Equal(1, permit.InFlight);
        Assert.Equal(0, permit.Available);

        var segunda = permit.AcquireAsync(CancellationToken.None);
        Assert.False(segunda.IsCompleted, "a segunda operação pesada não podia ter entrado.");

        primeira.Dispose();

        using var liberada = await segunda.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, permit.InFlight);
    }

    [Fact]
    public async Task OTetoConfiguradoEORespeitadoEONumeroDeLicencasSimultaneas()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 2);

        using var primeira = await permit.AcquireAsync(CancellationToken.None);
        using var segunda = await permit.AcquireAsync(CancellationToken.None);

        Assert.Equal(2, permit.InFlight);
        var terceira = permit.AcquireAsync(CancellationToken.None);
        Assert.False(terceira.IsCompleted);

        segunda.Dispose();
        using var liberada = await terceira.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, permit.InFlight);
    }

    /// <summary>
    /// Teto zero pararia a fábrica inteira sem produzir erro nenhum — todo trabalho pesado ficaria
    /// esperando para sempre, e o diagnóstico seria "está lento". O piso de 1 existe para que uma
    /// configuração errada não vire um sistema morto.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task TetoInvalidoCaiNoPisoEmVezDeParalisarAFabrica(int configurado)
    {
        using var permit = new HeavyWorkPermit(configurado);

        Assert.Equal(1, permit.MaxConcurrent);
        using var licenca = await permit.AcquireAsync(CancellationToken.None);
        Assert.Equal(1, permit.InFlight);
    }

    [Fact]
    public async Task CancelamentoNaoConsomeLicenca()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 1);
        using var ocupada = await permit.AcquireAsync(CancellationToken.None);

        using var cancelamento = new CancellationTokenSource();
        var esperando = permit.AcquireAsync(cancelamento.Token);
        await cancelamento.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => esperando);

        // O cancelamento não pode ter contado como entrada: quem desistiu de esperar não ocupou
        // a máquina.
        Assert.Equal(1, permit.InFlight);

        ocupada.Dispose();
        Assert.Equal(0, permit.InFlight);
        Assert.Equal(1, permit.Available);
    }

    /// <summary>
    /// A operação pesada explode. A licença TEM de voltar — e volta porque quem a tomou usa
    /// `using`, que é o contrato deste tipo.
    /// </summary>
    [Fact]
    public async Task OperacaoQueExplodeDevolveALicenca()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var licenca = await permit.AcquireAsync(CancellationToken.None);
            throw new InvalidOperationException("build explodiu");
        });

        Assert.Equal(0, permit.InFlight);
        Assert.Equal(1, permit.Available);

        // E a verificação seguinte entra imediatamente, sem esperar nada.
        var seguinte = permit.AcquireAsync(CancellationToken.None);
        Assert.True(seguinte.IsCompleted, "a licença ficou presa depois de uma falha.");
        (await seguinte).Dispose();
    }

    [Fact]
    public async Task DescartarDuasVezesNaoDevolveLicencaQueNaoExiste()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 1);

        var licenca = await permit.AcquireAsync(CancellationToken.None);
        licenca.Dispose();
        licenca.Dispose();

        // Devolver duas vezes inflaria o contador e o teto deixaria de valer — a trava existiria
        // no código e não na prática.
        Assert.Equal(1, permit.Available);
        Assert.Equal(0, permit.InFlight);
    }

    /// <summary>
    /// A prova da Onda 0.5: SETE builds solicitados com limite DOIS. Nunca mais de dois rodando,
    /// os demais em fila VISÍVEL (com rótulo, não num await anônimo), e todos concluem.
    /// </summary>
    [Fact]
    public async Task SeteBuildsComLimiteDoisRodamNoMaximoDoisPorVezESemPerderNenhum()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 2);
        var concluidos = 0;
        var picoSimultaneo = 0;
        var ativos = 0;
        var sync = new object();

        var builds = Enumerable.Range(1, 7).Select(async n =>
        {
            using var lease = await permit.AcquireAsync(
                CancellationToken.None, priority: 0, label: $"build-{n}");
            lock (sync) { ativos++; picoSimultaneo = Math.Max(picoSimultaneo, ativos); }
            await Task.Delay(30);
            lock (sync) { ativos--; concluidos++; }
        }).ToArray();

        // Enquanto roda, a fila é visível com rótulos.
        await Task.Delay(15);
        var (running, queued) = permit.Snapshot();
        Assert.True(running.Count <= 2, $"rodando: {running.Count}");
        Assert.All(running, label => Assert.StartsWith("build-", label, StringComparison.Ordinal));

        await Task.WhenAll(builds);

        Assert.Equal(7, concluidos);
        Assert.True(picoSimultaneo <= 2, $"pico foi {picoSimultaneo}; o limite é 2.");
        Assert.Equal(0, permit.Waiting);
        Assert.Equal(0, permit.InFlight);
    }

    /// <summary>
    /// Prioridade fura a fila quando abre vaga; dentro da mesma prioridade vale a chegada —
    /// inanição do card barato é o defeito clássico que ninguém vê até ele esperar a noite.
    /// </summary>
    [Fact]
    public async Task PrioridadeMaiorEntraPrimeiroQuandoAbreVaga()
    {
        using var permit = new HeavyWorkPermit(maxConcurrent: 1);
        var ordem = new List<string>();
        var sync = new object();

        var ocupante = await permit.AcquireAsync(CancellationToken.None, 0, "ocupante");

        var baixa = Task.Run(async () =>
        {
            using var lease = await permit.AcquireAsync(CancellationToken.None, 0, "baixa");
            lock (sync) ordem.Add("baixa");
        });
        await Task.Delay(30);
        var alta = Task.Run(async () =>
        {
            using var lease = await permit.AcquireAsync(CancellationToken.None, 10, "alta");
            lock (sync) ordem.Add("alta");
        });
        await Task.Delay(30);

        Assert.Equal(2, permit.Waiting);
        ocupante.Dispose();
        await Task.WhenAll(baixa, alta);

        Assert.Equal(["alta", "baixa"], ordem);
    }
}
