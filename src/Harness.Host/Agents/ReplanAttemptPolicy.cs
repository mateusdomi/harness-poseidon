using Harness.Persistence.Abstractions.WorkChain;

namespace Harness.Host.Agents;

/// <summary>
/// Uma tentativa EXERCEU a abordagem do card?
///
/// É a pergunta que a guarda do replanejamento faz antes de recusar a volta de um card escalado.
/// Replanejar duas vezes sobre a mesma abordagem só repete o fracasso — mas para isso a abordagem
/// precisa ter sido de fato exercida, e três coisas diferentes se pareciam com isso:
///
/// - morrer por infraestrutura (reinício do Host, conta sem cota) nunca julgou o enunciado;
/// - não produzir token nenhum é o mesmo caso, visto por outro sinal;
/// - <b>rodar, gastar token e não introduzir mudança nenhuma</b> também não exerceu abordagem —
///   não há o que evitar repetir, porque nada foi tentado.
///
/// A terceira é a que custou caro. A primeira versão da regra a mediu por
/// <c>CommitRefs.Count > 0</c>, e esse sinal é sempre verdadeiro: a colheita governada commita os
/// restos da worktree e devolve o HEAD dela mesmo quando não havia resto, então toda tentativa
/// colhida grava um <c>git-commit:</c> — para as vazias, o SHA da própria base. A regra existia,
/// passava nos testes e nunca disparava em produção.
///
/// Por isso a entrega entra aqui como <see cref="bool"/>? e não como contagem: <c>null</c> é
/// "não deu para apurar", e conta como ENTREGOU. Afrouxar uma guarda anti-laço por causa de uma
/// leitura que falhou é o pior default disponível.
/// </summary>
public static class ReplanAttemptPolicy
{
    public static bool ExercisedApproach(
        string? failureReason, long tokensOutput, bool? introducedChanges) =>
        !CardCircuitBreakerService.IsInfrastructureFailure(failureReason) &&
        tokensOutput > 0 &&
        introducedChanges != false;

    /// <summary>O cabeçalho do bloco de replanejamento dentro do corpo da instrução.</summary>
    public const string ReplanMarker = "## Replanejamento após escalonamento";

    /// <summary>
    /// Em que RODADA de replanejamento o card está, lido do próprio corpo da instrução.
    ///
    /// O teto contava OCORRÊNCIAS do marcador, e o marcador é herdado: a instrução corretiva copia
    /// o corpo anterior e acrescenta os achados, então um único replanejamento aparecia de novo em
    /// toda versão seguinte. Medido no card 01KZ4ZM2WYRFEHVNG05C4J3218: cinco blocos IDÊNTICOS,
    /// palavra por palavra, num corpo com quatro rodadas reais — a contagem media herança, não
    /// trabalho. Um proxy quebrado pela terceira vez neste mesmo bloco de código.
    ///
    /// Agora a rodada é DECLARADA no marcador (<c>(rodada N)</c>) em vez de inferida. Corpo legado,
    /// sem número, vale UMA rodada por mais vezes que o bloco apareça: a duplicação é prova de
    /// herança, não de tentativa — os blocos são idênticos.
    /// </summary>
    public static int ReadReplanRound(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var highest = 0;
        var declared = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (!trimmed.StartsWith(ReplanMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed[ReplanMarker.Length..].Trim();
            if (rest.StartsWith("(rodada ", StringComparison.Ordinal) &&
                rest.EndsWith(')') &&
                int.TryParse(rest[8..^1], out var round))
            {
                declared = true;
                highest = Math.Max(highest, round);
            }
            else
            {
                highest = Math.Max(highest, 1);
            }
        }

        return declared ? highest : Math.Min(highest, 1);
    }

    /// <summary>
    /// Desde quando contar rodadas gastas: o início do EPISÓDIO de replanejamento atual, não a
    /// história inteira do card.
    ///
    /// INC-EVAL-004: <c>ReplanEscalatedTaskAsync</c> devolve o card a <c>ready</c> e grava uma
    /// instrução nova com <c>(rodada N)</c>, mas <c>CountSpentRounds</c> lia o histórico de
    /// tentativas inteiro, sem nunca zerado — a contagem seguia ≥ <c>MaxRounds</c> e a guarda
    /// reescalava o card no mesmo ciclo do laço, dezenas de segundos depois da decisão humana ter
    /// sido aplicada. As duas regras (guarda anti-laço, replanejamento aprovado) estavam corretas
    /// isoladamente; juntas, tornavam a decisão do dono um no-op.
    ///
    /// As instruções chegam em ordem cronológica ascendente (a mais recente por último). A rodada
    /// declarada persiste através de correções — <c>PrepareCorrectionsAsync</c> preserva o corpo
    /// anterior e só reconstrói o cabeçalho — então caminhar para trás enquanto a rodada
    /// permanece a MESMA encontra exatamente a instrução do replanejamento que abriu o episódio
    /// atual, não importa quantas correções vieram depois dela. Round 0 (nunca replanejado)
    /// devolve <c>null</c>: quem chama usa o histórico inteiro, o comportamento de sempre.
    /// </summary>
    public static DateTimeOffset? CurrentReplanEpochStartedAt(
        IReadOnlyList<BoardInstructionRecord> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        if (instructions.Count == 0)
        {
            return null;
        }

        var currentRound = ReadReplanRound(instructions[^1].Body);
        if (currentRound == 0)
        {
            return null;
        }

        var epochStartedAt = instructions[^1].CreatedAt;
        for (var index = instructions.Count - 2; index >= 0; index--)
        {
            if (ReadReplanRound(instructions[index].Body) != currentRound)
            {
                break;
            }

            epochStartedAt = instructions[index].CreatedAt;
        }

        return epochStartedAt;
    }

    /// <summary>
    /// Remove os blocos de replanejamento do corpo, preservando todo o resto.
    ///
    /// Sem isto o enunciado ACUMULAVA: o card citado acima chegou ao ator com cinco cópias do mesmo
    /// bloco — "a abordagem anterior NÃO deve ser repetida... registre o bloqueio em vez de tentar
    /// de novo" — e nada indicava que eram a mesma ordem repetida. Um enunciado que cresce por
    /// acréscimo a cada volta não é o mesmo enunciado, e ninguém escolheu o que ele virou.
    ///
    /// Um bloco vai do marcador até o próximo cabeçalho <c>##</c> ou o fim do texto; os blocos de
    /// correção que vêm depois ficam intactos.
    /// </summary>
    public static string StripReplanBlocks(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = new List<string>(lines.Length);
        var inside = false;
        foreach (var line in lines)
        {
            if (line.TrimEnd().StartsWith(ReplanMarker, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                if (!line.StartsWith("##", StringComparison.Ordinal))
                {
                    continue;
                }

                inside = false;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept).TrimEnd('\n');
    }
}
