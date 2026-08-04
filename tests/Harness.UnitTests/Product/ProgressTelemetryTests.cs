using Harness.Modules.Workflows.Product;

namespace Harness.UnitTests.Product;

/// <summary>
/// A distinção que a plataforma não sabia fazer.
///
/// O único sinal de progresso que existia era <c>OutputTokens &gt; 0</c> — "o modelo escreveu
/// alguma coisa". Por esse critério, uma tentativa que resolveu seis dos oito problemas e uma que
/// bateu na mesma parede pela terceira vez são idênticas.
///
/// Nada aqui bloqueia, prioriza, replaneja ou escala. É medição, e é assim de propósito: a política
/// de "sem progresso" precisa ser desenhada sobre comportamento real observado num Golden Run.
/// </summary>
public sealed class ProgressTelemetryTests
{
    private static ProductEvidenceFinding Gap(ProductEvidenceKind kind, ProductEvidenceGap gap) =>
        new(kind, gap, $"{kind} está {gap}.");

    [Fact]
    public void OitoParaCincoParaDoisApareceComoProgresso()
    {
        var tentativa1 = Oito();
        var tentativa2 = tentativa1.Take(5).ToArray();
        var tentativa3 = tentativa1.Take(2).ToArray();

        var segunda = ProgressTelemetry.Compare(tentativa1, tentativa2);
        var terceira = ProgressTelemetry.Compare(tentativa2, tentativa3);

        Assert.Equal(3, segunda.Resolved);
        Assert.Equal(5, segunda.Repeated);
        Assert.Equal(0, segunda.New);
        Assert.Equal(3, segunda.Score);

        Assert.Equal(3, terceira.Resolved);
        Assert.Equal(2, terceira.Repeated);
        Assert.Equal(3, terceira.Score);
    }

    [Fact]
    public void OitoParaOitoParaOitoApareceComoEstagnacao()
    {
        var tentativa = Oito();

        var segunda = ProgressTelemetry.Compare(tentativa, tentativa);
        var terceira = ProgressTelemetry.Compare(tentativa, tentativa);

        foreach (var delta in (ProgressDelta[])[segunda, terceira])
        {
            Assert.Equal(0, delta.Resolved);
            Assert.Equal(8, delta.Repeated);
            Assert.Equal(0, delta.New);
            Assert.Equal(0, delta.Score);
        }
    }

    /// <summary>
    /// O caso ambíguo que o saldo sozinho esconderia: resolveu três e criou três. Trabalho houve;
    /// progresso, nenhum. Guardar as três contagens separadas é o que permite ver isso.
    /// </summary>
    [Fact]
    public void ResolverTresECriarTresNaoEProgresso()
    {
        var antes = Oito().Take(4).ToArray();
        var depois = new[]
        {
            Gap(ProductEvidenceKind.BackendPresent, ProductEvidenceGap.Missing),
            Gap(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Failed),
            Gap(ProductEvidenceKind.PersistenceVerified, ProductEvidenceGap.Untrusted),
            Gap(ProductEvidenceKind.SecurityScanPassed, ProductEvidenceGap.Missing),
        };

        var delta = ProgressTelemetry.Compare(antes, depois);

        Assert.Equal(0, delta.Score);
        Assert.True(delta.Resolved > 0);
        Assert.True(delta.New > 0);
        Assert.Equal(delta.Resolved, delta.New);
    }

    /// <summary>
    /// A chave é tipo + natureza da lacuna. A justificativa muda de redação a cada execução, e
    /// compará-la faria toda repetição parecer novidade — o medidor mediria o gerador de texto.
    /// </summary>
    [Fact]
    public void ARedacaoDoMotivoNaoTransformaRepeticaoEmNovidade()
    {
        var antes = new[]
        {
            new ProductEvidenceFinding(
                ProductEvidenceKind.BackendBuild, ProductEvidenceGap.Failed, "erro CS1002 na linha 12"),
        };
        var depois = new[]
        {
            new ProductEvidenceFinding(
                ProductEvidenceKind.BackendBuild, ProductEvidenceGap.Failed, "erro CS1513 na linha 40"),
        };

        var delta = ProgressTelemetry.Compare(antes, depois);

        Assert.Equal(1, delta.Repeated);
        Assert.Equal(0, delta.New);
        Assert.Equal(0, delta.Resolved);
    }

    /// <summary>
    /// A MESMA lacuna com natureza diferente é outro problema: `Missing` virou `Failed` significa
    /// que a evidência passou a existir e reprovou — houve movimento, e chamar isso de repetição
    /// esconderia justamente a mudança.
    /// </summary>
    [Fact]
    public void MudarANaturezaDaLacunaContaComoMovimento()
    {
        var antes = new[] { Gap(ProductEvidenceKind.OpenApiGenerated, ProductEvidenceGap.Missing) };
        var depois = new[] { Gap(ProductEvidenceKind.OpenApiGenerated, ProductEvidenceGap.Failed) };

        var delta = ProgressTelemetry.Compare(antes, depois);

        Assert.Equal(1, delta.Resolved);
        Assert.Equal(1, delta.New);
        Assert.Equal(0, delta.Repeated);
    }

    [Fact]
    public void PrimeiraAvaliacaoENomeadaComoLinhaDeBaseEmVezDeProgressoInventado()
    {
        var delta = ProgressTelemetry.Compare(null, Oito());

        Assert.True(delta.IsBaseline);
        Assert.Equal(0, delta.Before);
        Assert.Equal(8, delta.After);
        Assert.Equal(8, delta.New);
    }

    [Fact]
    public void EntregaQueFechouTodasAsLacunasTemSaldoIgualAoQueTinhaAntes()
    {
        var delta = ProgressTelemetry.Compare(Oito(), []);

        Assert.Equal(8, delta.Resolved);
        Assert.Equal(0, delta.After);
        Assert.Equal(8, delta.Score);
        Assert.Equal(0, delta.GateFailures);
    }

    private static ProductEvidenceFinding[] Oito() =>
    [
        Gap(ProductEvidenceKind.BackendBuild, ProductEvidenceGap.Failed),
        Gap(ProductEvidenceKind.FrontendBuild, ProductEvidenceGap.Failed),
        Gap(ProductEvidenceKind.OpenApiGenerated, ProductEvidenceGap.Missing),
        Gap(ProductEvidenceKind.PersistenceVerified, ProductEvidenceGap.Missing),
        Gap(ProductEvidenceKind.E2EJourneyPassed, ProductEvidenceGap.Missing),
        Gap(ProductEvidenceKind.AutomatedTestsPassed, ProductEvidenceGap.Failed),
        Gap(ProductEvidenceKind.SecurityScanPassed, ProductEvidenceGap.Missing),
        Gap(ProductEvidenceKind.RunbookPresent, ProductEvidenceGap.Missing),
    ];
}
