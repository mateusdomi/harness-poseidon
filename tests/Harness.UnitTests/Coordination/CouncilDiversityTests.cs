using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// D2 — assento é lente; conta é quem pensa.
///
/// Em 03/08/2026 a fase 4 produziu seis pareceres e todos saíram de `worker-antigravity-review`.
/// A mesa tinha seis lentes e uma inteligência: a diversidade existia só na instrução, e a
/// cegueira que ela deveria quebrar se repetiu seis vezes com aparência de consenso. Nada no
/// sistema acusava — o conselho reportava seis pareceres e seguia.
///
/// A resposta desta política não é bloquear. Bloquear devolveria o impasse de 03/08 com outro
/// nome, porque capacidade curta é a condição normal de uma frota de assinaturas. A resposta é
/// DECLARAR: o conselho libera dizendo em quantas cabeças aquilo foi pensado, e quem lê decide o
/// quanto vale.
/// </summary>
public sealed class CouncilDiversityTests
{
    private static CouncilOpinion Opinion(string seat, string? account, string? provider = null) =>
        new(seat, IsBlocking: false, HasConcern: false, "ok", IsOperational: false, account, provider);

    // ---- Serialização por capacidade ------------------------------------------------

    /// <summary>
    /// Uma conta elegível abre UM assento por vez. Abrir seis produziria seis cards disputando a
    /// mesma conta, cinco adiados a cada ciclo — a fase parada com aparência de trabalho.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    public void OTetoDeAssentosSegueONumeroDeContasDistintas(int accounts, int expected)
    {
        Assert.Equal(
            expected,
            AgentCouncilPolicy.MaximumConcurrentSeats(new CouncilCapacity(accounts, 1)));
    }

    /// <summary>
    /// O teto nunca é zero. Sem conta nenhuma quem recusa é o escalonador, com motivo tipado; se
    /// esta aritmética devolvesse zero, a fase travaria sem que nada dissesse por quê.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OTetoNuncaTravaAFasePorAritmetica(int accounts)
    {
        Assert.Equal(1, AgentCouncilPolicy.MaximumConcurrentSeats(new CouncilCapacity(accounts, 0)));
    }

    // ---- Medição da diversidade -----------------------------------------------------

    [Fact]
    public void SeisLentesNaMesmaContaSaoUmaInteligenciaSo()
    {
        var diversity = AgentCouncilPolicy.MeasureDiversity(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-tech-lead", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-security", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-dba-dados", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-qa", "worker-antigravity-review", "antigravity"),
        ]);

        Assert.Equal(6, diversity.Seats);
        Assert.Equal(1, diversity.DistinctAccounts);
        Assert.False(diversity.MeetsMinimumDiversity);
        Assert.Contains("DEGRADADO", diversity.Declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void DuasContasJaSatisfazemOPisoDeInteligencias()
    {
        var diversity = AgentCouncilPolicy.MeasureDiversity(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-codex-critic", "openai"),
            Opinion("playbook-tech-lead", "worker-antigravity-review", "antigravity"),
        ]);

        Assert.Equal(2, diversity.DistinctAccounts);
        Assert.Equal(2, diversity.DistinctProviders);
        Assert.True(diversity.MeetsMinimumDiversity);
        Assert.DoesNotContain("DEGRADADO", diversity.Declaration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duas contas do MESMO fornecedor bastam. Provedor distinto é preferência de qualidade;
    /// transformá-la em exigência devolveria o conselho à impossibilidade nas horas em que só um
    /// fornecedor responde — que é a condição real de uma frota de assinaturas.
    /// </summary>
    [Fact]
    public void DuasContasDoMesmoFornecedorSatisfazemOPiso()
    {
        var diversity = AgentCouncilPolicy.MeasureDiversity(
        [
            Opinion("playbook-product-owner", "chief-claude-primary", "anthropic"),
            Opinion("playbook-arquiteto", "worker-claude-secondary", "anthropic"),
            Opinion("playbook-tech-lead", "chief-claude-primary", "anthropic"),
        ]);

        Assert.True(diversity.MeetsMinimumDiversity);
        Assert.Equal(1, diversity.DistinctProviders);
    }

    /// <summary>
    /// Autoria desconhecida NUNCA vira diversidade otimista. Sem prova de qual conta escreveu, a
    /// resposta é "desconhecida" — arredondar para cima aqui seria mentir exatamente no campo que
    /// existe para impedir que se minta.
    /// </summary>
    [Fact]
    public void AutoriaDesconhecidaEDeclaradaComoDesconhecidaNaoComoSuficiente()
    {
        var diversity = AgentCouncilPolicy.MeasureDiversity(
        [
            Opinion("playbook-product-owner", null),
            Opinion("playbook-arquiteto", null),
            Opinion("playbook-tech-lead", null),
        ]);

        Assert.Equal(0, diversity.DistinctAccounts);
        Assert.False(diversity.MeetsMinimumDiversity);
        Assert.True(diversity.DiversityUnknown);
        Assert.Equal("council.cleared_diversity_unknown", diversity.ClearedReasonCode);
        Assert.Contains("DESCONHECIDA", diversity.Declaration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Um assento que não pôde ser ouvido não é uma lente: ele segura a fase, mas não conta como
    /// opinião nem como inteligência. Contá-lo inflaria a mesa com ausências.
    /// </summary>
    [Fact]
    public void AssentoOperacionalNaoContaComoLenteNemComoInteligencia()
    {
        var diversity = AgentCouncilPolicy.MeasureDiversity(
        [
            Opinion("playbook-product-owner", "chief-claude-primary", "anthropic"),
            Opinion("playbook-arquiteto", "worker-claude-secondary", "anthropic"),
            new("playbook-tech-lead", IsBlocking: true, HasConcern: false,
                "não pôde ser ouvido", IsOperational: true, "worker-codex-critic", "openai"),
        ]);

        Assert.Equal(2, diversity.Seats);
        Assert.Equal(2, diversity.DistinctAccounts);
    }

    // ---- Declaração na consolidação -------------------------------------------------

    /// <summary>
    /// A degradação LIBERA e é dita. É a diferença entre um conselho honesto e um carimbo: o
    /// veredito passa, e o motivo pelo qual ele vale menos vai junto, no código e no texto.
    /// </summary>
    [Fact]
    public void ConselhoDegradadoLiberaMasDeclaraADegradacao()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-tech-lead", "worker-antigravity-review", "antigravity"),
        ]);

        Assert.True(verdict.MayProceed);
        Assert.Equal("council.cleared_degraded", verdict.ReasonCode);
        Assert.Contains("DEGRADADO", verdict.Rationale, StringComparison.Ordinal);
        Assert.NotNull(verdict.Diversity);
        Assert.Equal(1, verdict.Diversity!.DistinctAccounts);
    }

    [Fact]
    public void ConselhoComDiversidadeSuficienteLiberaSemMarcaDeDegradacao()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-claude-secondary", "anthropic"),
            Opinion("playbook-tech-lead", "worker-antigravity-review", "antigravity"),
        ]);

        Assert.True(verdict.MayProceed);
        Assert.Equal("council.cleared", verdict.ReasonCode);
        Assert.Contains("2 inteligência(s) distinta(s)", verdict.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// Degradação não é atenuante. Um achado bloqueante segura a fase com uma inteligência ou com
    /// seis — evidência decide risco, e o tamanho da mesa não muda o que foi encontrado.
    /// </summary>
    [Fact]
    public void ADegradacaoNaoAtenuaUmAchadoBloqueante()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-antigravity-review", "antigravity"),
            new("playbook-security", IsBlocking: true, HasConcern: false, "falha explorável",
                IsOperational: false, "worker-antigravity-review", "antigravity"),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.blocking_finding", verdict.ReasonCode);
        Assert.Contains("DEGRADADO", verdict.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// E o piso de lentes continua acima de tudo: duas opiniões não são conselho, por mais contas
    /// distintas que tenham escrito.
    /// </summary>
    [Fact]
    public void OPisoDeLentesVemAntesDaDiversidade()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            Opinion("playbook-product-owner", "worker-antigravity-review", "antigravity"),
            Opinion("playbook-arquiteto", "worker-claude-secondary", "anthropic"),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.incomplete", verdict.ReasonCode);
    }
}
