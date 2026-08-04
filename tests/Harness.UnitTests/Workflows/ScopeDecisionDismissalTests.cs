using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Workflows;

/// <summary>
/// Um card que a fase deixou de exigir precisa poder fechar.
///
/// Sem isto o quadro contradiz o portão: a fase avança porque a obrigação foi cancelada por
/// decisão de escopo, e o card fica em "precisa de atenção" para sempre, pedindo uma decisão que
/// já foi tomada. Aconteceu na fase 5 da prova limpa em 04/08/2026 — quatro cards acusando
/// impedimento depois de a fase ter fechado. Dois registros do mesmo fato que não conversam
/// custam mais confiança do que qualquer um deles entrega sozinho.
///
/// A extensão é estreita de propósito: ator `system` e razão tipada. Quem cancela a obrigação é
/// quem decide escopo; este predicado apenas reconhece que isso aconteceu.
/// </summary>
public sealed class ScopeDecisionDismissalTests
{
    private static BoardTaskRecord Card(string boardState, string internalState) =>
        new(
            "tenant", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "project", null, "FEAT/T02 Interface",
            boardState, "low", null, null, 1,
            new BoardProgressRecord(0, 0, 0), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, null, 1, internalState, "solicitation", "demand", "5-Desenvolvimento",
            "agent_task");

    private static BoardTaskDismissCommand Command(string reason, string by = "system") =>
        new("tenant", "01ARZ3NDEKTSV4RRFFQ69G5FAV", reason, by, DateTimeOffset.UnixEpoch);

    private const string Scope = BoardTaskDismissalPolicy.ScopeDecisionReasonPrefix;

    /// <summary>O caso real: card escalado e bloqueado, cuja obrigação saiu do plano.</summary>
    [Fact]
    public void CardBloqueadoFechaQuandoAObrigacaoSaiuDoPlano()
    {
        Assert.True(BoardTaskDismissalPolicy.MayDismiss(
            Card("blocked", "escalated"),
            Command($"{Scope} fora da fronteira backend-only, ADR 15ee768")));
    }

    /// <summary>
    /// Vale em qualquer posição do quadro: a decisão de escopo não depende de onde o card parou.
    /// </summary>
    [Theory]
    [InlineData("development", "running")]
    [InlineData("review", "awaiting_review")]
    [InlineData("corrections", "running")]
    public void ADecisaoDeEscopoNaoDependeDeOndeOCardParou(string boardState, string internalState)
    {
        Assert.True(BoardTaskDismissalPolicy.MayDismiss(
            Card(boardState, internalState), Command($"{Scope} retirado do plano")));
    }

    /// <summary>
    /// SEM o prefixo, nada muda: um card bloqueado por trabalho ruim continua exigindo
    /// replanejamento. É essa recusa que impede o encerramento de virar vassoura para debaixo do
    /// tapete — fechar sem entregar precisa ser uma decisão nomeada, não um atalho.
    /// </summary>
    [Theory]
    [InlineData("consumiu as 2 rodadas que orçamos")]
    [InlineData("scope decision")]
    [InlineData("decisão de escopo")]
    [InlineData("")]
    public void SemARazaoTipadaOCardBloqueadoNaoFecha(string reason)
    {
        Assert.False(BoardTaskDismissalPolicy.MayDismiss(Card("blocked", "escalated"), Command(reason)));
    }

    /// <summary>
    /// E o ator precisa ser o sistema. Cancelar a obrigação é o ato que autoriza; um agente
    /// pedindo encerramento direto estaria redefinindo o que precisava ser feito.
    /// </summary>
    [Theory]
    [InlineData("agent")]
    [InlineData("chief")]
    [InlineData("user")]
    public void SoOSistemaEncerraPorDecisaoDeEscopo(string changedByKind)
    {
        Assert.False(BoardTaskDismissalPolicy.MayDismiss(
            Card("blocked", "escalated"),
            Command($"{Scope} retirado do plano", changedByKind)));
    }

    /// <summary>As permissões que já existiam continuam valendo, intactas.</summary>
    [Theory]
    [InlineData("backlog")]
    [InlineData("ready")]
    public void TrabalhoAindaInativoContinuaDescartavel(string boardState)
    {
        Assert.True(BoardTaskDismissalPolicy.MayDismiss(
            Card(boardState, "draft"), Command("nao vamos fazer", "user")));
    }
}
