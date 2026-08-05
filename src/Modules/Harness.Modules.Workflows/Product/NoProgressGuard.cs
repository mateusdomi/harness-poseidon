namespace Harness.Modules.Workflows.Product;

/// <summary>O que a proteção de quota decidiu sobre repetir a tentativa.</summary>
/// <param name="BlindRetryForbidden">
/// Repetir do mesmo jeito está proibido. NÃO significa parar: significa que a próxima tentativa
/// precisa ser diferente da anterior em alguma coisa que importe.
/// </param>
/// <param name="ConsecutiveStalledAttempts">Quantas avaliações seguidas não fecharam lacuna alguma.</param>
/// <param name="DominantGaps">As lacunas que se repetem. É o que a próxima tentativa precisa atacar.</param>
public sealed record NoProgressVerdict(
    bool BlindRetryForbidden,
    int ConsecutiveStalledAttempts,
    IReadOnlyList<string> DominantGaps,
    string Reason)
{
    public const string ReasonCode = "product:blind_retry_forbidden";

    public static NoProgressVerdict Allowed(string reason) => new(false, 0, [], reason);
}

/// <summary>
/// A trava que impede a fábrica de gastar quota repetindo o mesmo erro.
///
/// A evidência que a justifica é do run real: um único card de interface acumulou <b>7 tentativas e
/// 0 aprovações</b>, e outro somou 5. Cada uma consumiu conta, contexto e cota para bater na mesma
/// parede — e o único sinal de progresso que a plataforma tinha era "o modelo emitiu tokens", que
/// todas as sete satisfizeram.
///
/// <b>O estado é DERIVADO do ledger, não guardado em memória.</b> A decisão sai dos conjuntos de
/// evidência já persistidos, o que resolve de graça o requisito de sobreviver a reinício do Host:
/// não há contador para zerar. Foi a razão de não criar tabela nova — o dado necessário já estava
/// gravado, só não estava sendo lido.
///
/// <b>Proibir repetição cega não é desistir.</b> O veredito diz que a PRÓXIMA tentativa não pode ser
/// idêntica; o que fazer com isso — replanejar, enriquecer contexto, trocar agente elegível,
/// corrigir o card ou escalar — é decisão de quem orquestra, e não deste tipo.
/// </summary>
public static class NoProgressGuard
{
    /// <summary>
    /// Avaliações seguidas sem fechar nenhuma lacuna a partir das quais repetir vira desperdício.
    /// Duas, e não três: a segunda repetição já é sinal, e a terceira custa uma conta inteira.
    /// </summary>
    public const int DefaultStalledThreshold = 2;

    /// <summary>
    /// Decide a partir do histórico de vereditos do projeto, do mais RECENTE para o mais antigo.
    ///
    /// A comparação é por impressão digital de lacuna — tipo + natureza —, nunca pelo texto do
    /// diagnóstico. O texto muda de redação a cada execução do modelo, e compará-lo faria toda
    /// repetição parecer novidade, que é exatamente o modo de falha que esta trava existe para
    /// impedir.
    /// </summary>
    public static NoProgressVerdict Evaluate(
        IReadOnlyList<IReadOnlyList<ProductEvidenceFinding>> historyNewestFirst,
        int stalledThreshold = DefaultStalledThreshold)
    {
        ArgumentNullException.ThrowIfNull(historyNewestFirst);

        if (historyNewestFirst.Count < 2)
        {
            return NoProgressVerdict.Allowed(
                "Não há duas avaliações para comparar: a primeira tentativa nunca é repetição.");
        }

        var stalled = 0;
        IReadOnlyList<string> dominant = [];

        // Cada par consecutivo (mais nova, anterior) é uma transição. Ela é ESTAGNADA quando nada
        // do que estava aberto foi fechado — resolver três e criar três também é estagnação, e é o
        // caso que um saldo simples esconderia.
        for (var index = 0; index + 1 < historyNewestFirst.Count; index++)
        {
            var delta = ProgressTelemetry.Compare(
                historyNewestFirst[index + 1], historyNewestFirst[index]);

            if (delta.Resolved > 0 || delta.Repeated == 0)
            {
                break;
            }

            stalled++;
            dominant = delta.RepeatedKeys;
        }

        return stalled >= Math.Max(1, stalledThreshold)
            ? new NoProgressVerdict(
                true,
                stalled,
                dominant,
                $"{stalled} avaliações seguidas não fecharam nenhuma lacuna, e as mesmas " +
                $"{dominant.Count} continuam abertas ({string.Join(", ", dominant.Take(4))}" +
                $"{(dominant.Count > 4 ? "…" : string.Empty)}). Repetir a tentativa do mesmo jeito " +
                "gastaria cota para chegar ao mesmo lugar: a próxima precisa mudar o card, o " +
                "contexto ou o executor.")
            : NoProgressVerdict.Allowed(
                stalled == 0
                    ? "A última avaliação fechou lacuna: houve progresso."
                    : $"{stalled} avaliação sem progresso, abaixo do limite de {stalledThreshold}.");
    }
}
