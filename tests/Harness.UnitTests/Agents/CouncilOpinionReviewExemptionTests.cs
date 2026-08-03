using Harness.Host.Agents;
using Harness.Modules.Coordination.Application;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Agents;

/// <summary>
/// D1 — o parecer do Conselho não passa por revisão independente, porque ele JÁ É a revisão.
///
/// Este é o único ponto do produto em que um card fecha sem segundo par de olhos, e por isso a
/// dispensa precisa ser estreita, explícita e testada. O que a torna legítima não é conveniência:
/// é que exigir revisão de um parecer cria uma recursão cujo custo é de ELENCO. O parecer só pode
/// ser escrito por conta de papel `critic` — é quem tem o claim `docs/conselho/**` —, então cada
/// assento consome uma conta critic como ator e a revisão dele exige outra. Com duas contas
/// critic e uma delas sem cota, os seis assentos da fase 4 entregaram o parecer em 03/08/2026 e
/// escalaram com `critic.none_available`: o conselho era impossível, e com ele a fase 5.
///
/// O controle que permanece é a CONSOLIDAÇÃO: um único veredito bloqueante segura a fase, o piso
/// de lentes distintas continua valendo e todo dissenso vai para o ledger.
/// </summary>
public sealed class CouncilOpinionReviewExemptionTests
{
    private static BoardTaskRecord Card(string cardType, string title = "card") =>
        new(
            "tenant", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "project", null, title, "review",
            "low", null, null, 1,
            new BoardProgressRecord(0, 0, 0), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, null, 1, "awaiting_review",
            "solicitation", "demand", "4-Planejamento", cardType);

    [Fact]
    public void OParecerDoConselhoEDispensadoDaRevisaoPorPar()
    {
        Assert.True(ChiefBacklogLoopService.IsCouncilOpinionCard(Card("revisao")));
    }

    /// <summary>
    /// A dispensa é a exceção, não a regra. Qualquer outro tipo de card continua exigindo revisão
    /// por agente distinto — inclusive o documental, que é o que a fábrica mais produz.
    /// </summary>
    [Theory]
    [InlineData("agent_task")]
    [InlineData("documento")]
    [InlineData("historia")]
    [InlineData("tarefa")]
    [InlineData("bug")]
    [InlineData("adr")]
    [InlineData("gate")]
    [InlineData("incidente")]
    [InlineData("chamado")]
    [InlineData("feature")]
    [InlineData("human_gate")]
    [InlineData("spike")]
    [InlineData("decision")]
    public void NenhumOutroTipoDeCardEDispensado(string cardType)
    {
        Assert.False(ChiefBacklogLoopService.IsCouncilOpinionCard(Card(cardType)));
    }

    /// <summary>
    /// O discriminador é comparação ORDINAL exata. Um tipo parecido não pode herdar a dispensa por
    /// acidente de maiúscula ou de prefixo — a exceção que fecha card sem revisão é o último lugar
    /// do produto onde uma comparação tolerante seria aceitável.
    /// </summary>
    [Theory]
    [InlineData("Revisao")]
    [InlineData("REVISAO")]
    [InlineData("revisao-tecnica")]
    [InlineData("pre-revisao")]
    [InlineData("revisão")]
    public void UmTipoPARECIDONaoHerdaADispensa(string cardType)
    {
        Assert.False(ChiefBacklogLoopService.IsCouncilOpinionCard(Card(cardType)));
    }

    /// <summary>
    /// A dispensa vale pelo TIPO, nunca pelo título. Um card comum que mencione o conselho no
    /// enunciado — e há muitos, porque o conselho é assunto da fase 4 — continua sendo revisado.
    /// </summary>
    [Theory]
    [InlineData("4-Planejamento — Conselho: parecer de playbook-qa — ciclo 1")]
    [InlineData("Corrigir achado do Conselho")]
    [InlineData("Conselho")]
    public void OTituloNaoConcedeDispensa(string title)
    {
        Assert.False(ChiefBacklogLoopService.IsCouncilOpinionCard(Card("agent_task", title)));
    }

    /// <summary>
    /// A garantia de fundo: dispensar o parecer só é defensável porque a consolidação continua
    /// sendo um portão de verdade. Um único bloqueante segura a fase mesmo com todos os outros
    /// pareceres liberando — maioria decide preferência, evidência decide risco.
    /// </summary>
    [Fact]
    public void ADispensaNaoAfrouxaAConsolidacao()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            new CouncilOpinion("playbook-product-owner", IsBlocking: false, HasConcern: false, "ok"),
            new CouncilOpinion("playbook-arquiteto", IsBlocking: false, HasConcern: false, "ok"),
            new CouncilOpinion("playbook-tech-lead", IsBlocking: false, HasConcern: false, "ok"),
            new CouncilOpinion("playbook-security", IsBlocking: true, HasConcern: false, "achado"),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.blocking_finding", verdict.ReasonCode);
    }

    /// <summary>
    /// E o piso continua valendo: dispensar a revisão não dispensa o quórum. Menos de três lentes
    /// não é conselho — é opinião com aparência de conselho.
    /// </summary>
    [Fact]
    public void ADispensaNaoAfrouxaOPiso()
    {
        var verdict = AgentCouncilPolicy.Consolidate(
        [
            new CouncilOpinion("playbook-product-owner", IsBlocking: false, HasConcern: false, "ok"),
            new CouncilOpinion("playbook-arquiteto", IsBlocking: false, HasConcern: false, "ok"),
        ]);

        Assert.False(verdict.MayProceed);
        Assert.Equal("council.incomplete", verdict.ReasonCode);
    }
}
