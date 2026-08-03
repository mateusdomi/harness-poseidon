using Harness.Modules.Coordination.Application;

namespace Harness.UnitTests.Coordination;

/// <summary>
/// §28 — o conselho é convocado sob demanda: núcleo obrigatório, assentos de risco/domínio
/// conforme o projeto, e override da Bruna.
///
/// Antes a mesa era fixa em cinco especialistas para todo planejamento. O custo maior não era
/// a cota: um parecer de segurança sobre um projeto sem superfície externa é ruído, e ruído
/// repetido é como uma sala inteira aprende a assinar sem ler.
/// </summary>
public sealed class AgentCouncilSeatSelectionTests
{
    private static string[] Keys(IReadOnlyList<CouncilSeat> seats) =>
        [.. seats.Select(seat => seat.PersonaKey)];

    [Fact]
    public void ONucleoSentaSempre()
    {
        var seats = AgentCouncilPolicy.SelectSeats(new CouncilContext());

        Assert.Equal(
            ["playbook-product-owner", "playbook-arquiteto", "playbook-tech-lead"],
            Keys(seats));
    }

    /// <summary>
    /// Mesmo a mesa mínima respeita o piso: três lentes distintas é o menor número em que uma
    /// divergência isolada aparece como divergência, e não como maioria.
    /// </summary>
    [Fact]
    public void AMesaMinimaAindaRespeitaOPiso()
    {
        var seats = AgentCouncilPolicy.SelectSeats(new CouncilContext());

        Assert.True(seats.Count >= AgentCouncilPolicy.MinimumCouncil);
        Assert.Equal(seats.Count, Keys(seats).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SegurancaSentaQuandoHaSuperficieExternaOuDadoSensivel()
    {
        Assert.Contains(
            "playbook-security",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(ExternalSurface: true))));
        Assert.Contains(
            "playbook-security",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(SensitiveData: true))));
        Assert.DoesNotContain(
            "playbook-security",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(Persistence: true))));
    }

    [Fact]
    public void DadosSentaQuandoHaPersistenciaMigracaoVolumeOuAnalytics()
    {
        Assert.Contains(
            "playbook-dba-dados",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(Persistence: true))));
        Assert.Contains(
            "playbook-dba-dados",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(Analytics: true))));
    }

    [Fact]
    public void OperacaoSentaQuandoHaDeployInfraObservabilidadeOuDisponibilidade()
    {
        Assert.Contains(
            "playbook-devops",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(Deployment: true))));
        Assert.DoesNotContain(
            "playbook-devops",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext())));
    }

    [Fact]
    public void QualidadeSentaQuandoHaCriterioTestavelOuEstrategiaDeQualidade()
    {
        Assert.Contains(
            "playbook-qa",
            Keys(AgentCouncilPolicy.SelectSeats(new CouncilContext(TestableCriteria: true))));
    }

    /// <summary>
    /// Um projeto com todas as superfícies convoca a mesa inteira — a política reduz por
    /// ausência de justificativa, não por economia.
    /// </summary>
    [Fact]
    public void UmProjetoQueJustificaTudoConvocaAMesaInteira()
    {
        var seats = AgentCouncilPolicy.SelectSeats(new CouncilContext(
            ExternalSurface: true, Persistence: true, Deployment: true, TestableCriteria: true));

        Assert.Equal(AgentCouncilPolicy.Seats.Count, seats.Count);
    }

    /// <summary>A Bruna pode convocar competência que a tabela não previu.</summary>
    [Fact]
    public void OOverrideDaBrunaAcrescentaUmEspecialista()
    {
        var seats = AgentCouncilPolicy.SelectSeats(
            new CouncilContext(),
            [new CouncilSeat("playbook-acessibilidade", "Isto é usável por quem não enxerga?")]);

        Assert.Contains("playbook-acessibilidade", Keys(seats));
        Assert.Equal(4, seats.Count);
    }

    /// <summary>
    /// Override AMPLIA e nunca reduz: a Bruna decide quem mais precisa opinar, não quem deixa de
    /// opinar. Repetir um assento do núcleo não o duplica nem o substitui.
    /// </summary>
    [Fact]
    public void OOverrideNaoRemoveNemDuplicaONucleo()
    {
        var seats = AgentCouncilPolicy.SelectSeats(
            new CouncilContext(),
            [new CouncilSeat("playbook-arquiteto", "outra lente qualquer")]);

        Assert.Equal(3, seats.Count);
        Assert.Contains("playbook-arquiteto", Keys(seats));
    }

    /// <summary>Mesmo contexto, mesma mesa: o conselho precisa ser reproduzível.</summary>
    [Fact]
    public void OMesmoContextoProduzSempreAMesmaMesa()
    {
        var context = new CouncilContext(ExternalSurface: true, Persistence: true);

        Assert.Equal(
            Keys(AgentCouncilPolicy.SelectSeats(context)),
            Keys(AgentCouncilPolicy.SelectSeats(context)));
    }
}
