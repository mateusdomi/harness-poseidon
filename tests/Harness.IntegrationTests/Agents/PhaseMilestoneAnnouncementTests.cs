using Harness.Host.Agents;

namespace Harness.IntegrationTests.Agents;

/// <summary>
/// O aviso de etapa concluída é idempotente contra reinício do Host: a própria conversa é o
/// registro durável do que já foi dito. O reconhecimento é a parte delicada — e foi onde o defeito
/// apareceu em execução real.
/// </summary>
public sealed class PhaseMilestoneAnnouncementTests
{
    [Fact]
    public void OAvisoDeUmaEtapaNaoSilenciaAEtapaSeguinte()
    {
        // Defeito observado: o aviso da Triagem diz "Sigo agora para **2-Descoberta**". Reconhecer
        // aviso repetido só pelo NOME da etapa fazia essa menção passar por aviso já publicado da
        // Descoberta — que então fechava em silêncio, justamente o problema que a feature existe
        // para resolver.
        var published = new[]
        {
            ChiefBacklogLoopService.MilestoneHeadingFor("1-Triagem") +
            " ✅\n\nSigo agora para **2-Descoberta**. Assim que ela fechar, te aviso de novo.",
        };

        Assert.True(ChiefBacklogLoopService.MilestoneAlreadyAnnounced(published, "1-Triagem"));
        Assert.False(ChiefBacklogLoopService.MilestoneAlreadyAnnounced(published, "2-Descoberta"));
    }

    [Fact]
    public void UmaEtapaJaAnunciadaNaoEAnunciadaDeNovoAposReinicio()
    {
        var published = new[]
        {
            ChiefBacklogLoopService.MilestoneHeadingFor("2-Descoberta") + " ✅\n\nO que ficou pronto:\n- PRD",
        };

        Assert.True(ChiefBacklogLoopService.MilestoneAlreadyAnnounced(published, "2-Descoberta"));
    }

    [Fact]
    public void MensagemComumDoUsuarioNaoContaComoAviso()
    {
        Assert.False(ChiefBacklogLoopService.MilestoneAlreadyAnnounced(
            ["Oi Bruna, como está a 2-Descoberta?"], "2-Descoberta"));
    }
}
