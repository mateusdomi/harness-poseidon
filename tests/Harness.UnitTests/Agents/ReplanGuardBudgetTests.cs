using Harness.Host.Agents;
using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.UnitTests.Agents;

/// <summary>
/// OPS-070 — o orçamento anti-laço puniu quatro cards por uma parede que era NOSSA.
///
/// Os cards de implementação da fase 5 entregaram diff vazio duas vezes cada, por dois defeitos
/// do próprio sistema: o cabeçalho da instrução mandava um Product Owner — cujo escopo nega
/// `src/**` — escrever backend (OPS-069), e o plano exigia uma decisão de arquitetura que nenhum
/// card podia executar (OPS-072). Quando os dois consertos entraram no ar, o replanejamento único
/// de cada card já estava gasto e eles não tinham mais volta automática.
///
/// Duas correções, ambas estreitas de propósito — nenhuma toca o limiar do circuito:
///
/// 1. "Tentou" passa a significar que algo foi ENTREGUE. Uma tentativa que rodou, gastou token e
///    não introduziu mudança nenhuma não exerceu abordagem alguma — não há o que evitar repetir.
///    É o mesmo raciocínio que o código já aceita para falha de infraestrutura.
///
///    A PRIMEIRA VERSÃO DESTA REGRA NÃO FUNCIONAVA (OPS-078). Ela media a entrega por
///    `CommitRefs.Count > 0`, e esse sinal é sempre verdadeiro — a colheita governada commita os
///    restos da worktree e devolve o HEAD dela mesmo quando não havia resto. Medido nos quatro
///    cards: onze tentativas, três referências cada, inclusive as de diff vazio. A prova de
///    entrega é o diff da branch da tentativa, e a dúvida (`null`) conta como entrega.
///
/// 2. O teto de rodadas operacionais lê a rodada DECLARADA no corpo da instrução. Contava
///    ocorrências do marcador, e o marcador é HERDADO: a instrução corretiva copia o corpo
///    anterior, então um único replanejamento reaparecia em toda versão seguinte (OPS-080).
/// </summary>
public sealed class ReplanGuardBudgetTests
{
    private const string Marker = ReplanAttemptPolicy.ReplanMarker;

    // A regra vem da PRODUÇÃO. A versão anterior deste arquivo reimplementava o predicado aqui —
    // e foi por isso que ela ficou verde enquanto o código real nunca disparava: o teste media a
    // cópia, não a regra. Um teste que reescreve a regra prova apenas que sabe escrevê-la.
    private static int RealAttempts(params (string? Reason, long Tokens, bool? Introduced)[] attempts) =>
        attempts.Count(a => ReplanAttemptPolicy.ExercisedApproach(a.Reason, a.Tokens, a.Introduced));

    // ---- "Tentou" significa entregou -------------------------------------------------

    /// <summary>
    /// O caso que travou a fase 5: token gasto, nada entregue. O ator falou e não escreveu —
    /// a abordagem nunca foi exercida, então não há o que o replanejamento precise evitar.
    /// </summary>
    [Fact]
    public void TentativaQueGastouTokenESemCommitNaoContaComoAbordagemExercida()
    {
        Assert.Equal(0, RealAttempts((null, 11607, false)));
    }

    /// <summary>
    /// E o simétrico, que é o que impede isto de virar desculpa: entregou, exerceu a abordagem,
    /// gastou o replanejamento. Um card que de fato tentou e falhou não volta de graça.
    /// </summary>
    [Fact]
    public void TentativaQueENTREGOUContaEGastaOReplanejamento()
    {
        Assert.Equal(1, RealAttempts((null, 67748, true)));
    }

    /// <summary>
    /// O default que impede a leitura falha de virar permissão. Branch podada, repositório fora
    /// do lugar, git com erro: "não sei" conta como entrega e o card NÃO ganha replanejamento de
    /// graça. É a diferença entre afrouxar uma guarda por decisão e afrouxá-la por acidente.
    /// </summary>
    [Fact]
    public void DiffQueNaoPodeSerApuradoContaComoEntrega()
    {
        Assert.Equal(1, RealAttempts((null, 45457, null)));
    }

    /// <summary>Falha de infraestrutura continua não contando — a regra anterior segue valendo.</summary>
    [Theory]
    [InlineData("executor.quota_exhausted")]
    [InlineData("run.host_restart")]
    [InlineData("executor.authentication_required")]
    public void FalhaDeInfraestruturaContinuaNaoContando(string reason)
    {
        Assert.Equal(0, RealAttempts((reason, 500, true)));
    }

    /// <summary>
    /// Entrega sem consumo de modelo não conta: o que se mede aqui é a abordagem ter sido
    /// exercida, e sem token nenhum não houve raciocínio a evitar repetir.
    /// </summary>
    [Fact]
    public void SoContaQuandoHOUVEEntregaEConsumo()
    {
        Assert.Equal(0, RealAttempts((null, 0, true)));
        Assert.Equal(0, RealAttempts((null, 0, false)));
    }

    // ---- O teto lê a RODADA declarada, e não ocorrências do marcador -----------------

    /// <summary>
    /// O estado real do card 01KZ4ZM2WYRFEHVNG05C4J3218: cinco blocos IDÊNTICOS, palavra por
    /// palavra, porque a instrução corretiva copia o corpo anterior. Contar ocorrências media
    /// herança; corpo legado sem número declarado vale UMA rodada.
    /// </summary>
    [Fact]
    public void CorpoLegadoComMarcadorRepetidoValeUmaRodada()
    {
        var body = string.Join("\n\n", Enumerable.Repeat($"{Marker}\nA abordagem anterior...", 5));

        Assert.Equal(1, ReplanAttemptPolicy.ReadReplanRound(body));
    }

    /// <summary>Sem replanejamento nenhum não há rodada — o card nunca voltou.</summary>
    [Fact]
    public void CorpoSemMarcadorNaoTemRodada()
    {
        Assert.Equal(0, ReplanAttemptPolicy.ReadReplanRound("corpo comum\n## Correções exigidas"));
    }

    /// <summary>
    /// E o simétrico, sem o qual a leitura acima viraria a saída fácil: rodada DECLARADA é lida
    /// como está, e o teto continua existindo.
    /// </summary>
    [Fact]
    public void RodadaDeclaradaEhLidaComoEsta()
    {
        Assert.Equal(4, ReplanAttemptPolicy.ReadReplanRound($"x\n\n{Marker} (rodada 4)\ntexto"));
    }

    /// <summary>
    /// O bloco antigo sai e o resto do enunciado fica intacto — inclusive as correções que vieram
    /// DEPOIS dele, que são o trabalho de revisão e não podem ser engolidas pela limpeza.
    /// </summary>
    [Fact]
    public void RemocaoDoBlocoPreservaOResto()
    {
        var body =
            $"cabeçalho do card\n\n{Marker} (rodada 2)\nlinha 1 do replan\nlinha 2\n\n" +
            "## Correções exigidas pelo review independente\nachado P0";

        var stripped = ReplanAttemptPolicy.StripReplanBlocks(body);

        Assert.DoesNotContain("linha 1 do replan", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, stripped, StringComparison.Ordinal);
        Assert.Contains("cabeçalho do card", stripped, StringComparison.Ordinal);
        Assert.Contains("achado P0", stripped, StringComparison.Ordinal);
    }

    // ---- INC-EVAL-004: o orçamento de rodadas é do EPISÓDIO, não do card inteiro -----

    private static readonly DateTimeOffset Day1 = DateTimeOffset.Parse(
        "2026-08-06T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static BoardInstructionRecord Instruction(string body, DateTimeOffset createdAt, int version = 1) =>
        new("tenant", $"instr-{version}", "task", version, body, "chief", null, createdAt);

    /// <summary>Card nunca replanejado: sem marcador, sem época — o chamador usa a história inteira.</summary>
    [Fact]
    public void CardNuncaReplanejadoNaoTemEpocaEUsaAHistoriaInteira()
    {
        var instructions = new[] { Instruction("corpo comum, sem marcador", Day1) };

        Assert.Null(ReplanAttemptPolicy.CurrentReplanEpochStartedAt(instructions));
    }

    /// <summary>
    /// O caso do INC-EVAL-004: o dono aprova o replanejamento, a instrução nova é gravada — a
    /// época do orçamento começa NAQUELE instante, não no início do card.
    /// </summary>
    [Fact]
    public void UmUnicoReplanejamentoAbreAEpocaNaSuaPropriaData()
    {
        var replanAt = Day1.AddDays(1);
        var instructions = new[]
        {
            Instruction("cabeçalho\n\ncorpo original, nunca replanejado", Day1),
            Instruction($"cabeçalho\n\n{Marker} (rodada 1)\ntexto", replanAt, version: 2),
        };

        Assert.Equal(replanAt, ReplanAttemptPolicy.CurrentReplanEpochStartedAt(instructions));
    }

    /// <summary>
    /// Correções DEPOIS do replanejamento preservam o corpo (e o marcador) — a época continua
    /// sendo a do replanejamento original, não a da correção mais recente. Sem isto, cada
    /// correção reabriria o orçamento de rodadas de graça.
    /// </summary>
    [Fact]
    public void CorrecoesAposOReplanejamentoNaoMovemAEpocaParaFrente()
    {
        var replanAt = Day1.AddDays(1);
        var instructions = new[]
        {
            Instruction("cabeçalho\n\ncorpo original, nunca replanejado", Day1),
            Instruction($"cabeçalho\n\n{Marker} (rodada 1)\ntexto", replanAt, version: 2),
            Instruction(
                $"cabeçalho\n\n{Marker} (rodada 1)\ntexto\n\n## Correções exigidas\nachado",
                replanAt.AddHours(3), version: 3),
        };

        Assert.Equal(replanAt, ReplanAttemptPolicy.CurrentReplanEpochStartedAt(instructions));
    }

    /// <summary>
    /// Um SEGUNDO replanejamento — o card re-escalou e o dono aprovou de novo — abre uma época
    /// NOVA a partir da rodada 2, sem herdar a data da primeira. Cada aprovação humana reabre o
    /// orçamento do zero, não acumula com a anterior.
    /// </summary>
    [Fact]
    public void UmSegundoReplanejamentoAbreUmaEpocaNovaSemHerdarADoPrimeiro()
    {
        var firstReplanAt = Day1.AddDays(1);
        var secondReplanAt = Day1.AddDays(5);
        var instructions = new[]
        {
            Instruction("cabeçalho\n\ncorpo original, nunca replanejado", Day1),
            Instruction($"cabeçalho\n\n{Marker} (rodada 1)\ntexto", firstReplanAt, version: 2),
            Instruction(
                $"cabeçalho\n\n{Marker} (rodada 1)\ntexto\n\n## Correções exigidas\nachado",
                firstReplanAt.AddHours(3), version: 3),
            Instruction($"cabeçalho\n\n{Marker} (rodada 2)\ntexto novo", secondReplanAt, version: 4),
        };

        Assert.Equal(secondReplanAt, ReplanAttemptPolicy.CurrentReplanEpochStartedAt(instructions));
    }
}
