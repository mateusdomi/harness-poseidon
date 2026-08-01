namespace Harness.Modules.Coordination.Application;

/// <summary>Um modo de falha recorrente observado, com quantas vezes ele apareceu.</summary>
public sealed record MastObservation(string FailureModeCode, MastCategory Category, int Occurrences);

/// <summary>
/// O ajuste que a distribuição MAST recomenda para a PRÓXIMA decomposição, com a evidência que o
/// justifica. Nunca é um número solto: quem decide precisa poder citar o que viu.
/// </summary>
public sealed record MastCorrection(
    bool HasSignal,
    int ExtraReviewDepth,
    bool PreferSmallerSlices,
    bool RequireExplicitAcceptanceCriteria,
    string ReasonCode,
    string Evidence);

/// <summary>
/// Fase 1D: a distribuição MAST vira ALAVANCA de correção no planejamento.
///
/// Classificar modo de falha já era feito (B1/F16) e morria como telemetria: um painel que ninguém
/// consultava na hora de decidir. O ponto da taxonomia nunca foi medir — é que cada categoria pede
/// uma correção DIFERENTE, e aplicar a correção errada é pior do que não corrigir:
///
/// * especificação → o pedido estava ambíguo: fatiar menor e exigir critério de aceite explícito;
/// * desalinhamento → a coordenação falhou: fatias menores e mais independentes reduzem a
///   superfície de colisão, mas revisar mais fundo não resolve conflito entre agentes;
/// * verificação → o trabalho terminou sem ninguém conferir: aí sim revisar mais fundo.
///
/// Política PURA e determinística: a mesma distribuição produz sempre a mesma recomendação, e a
/// decisão que ela informa pode citar a evidência que a produziu.
/// </summary>
public static class MastCorrectionPolicy
{
    /// <summary>
    /// Abaixo disto não há sinal, há ruído. Duas ocorrências do mesmo modo já são padrão; uma é
    /// acidente, e reagir a acidente faz o planejamento oscilar sem aprender nada.
    /// </summary>
    public const int RecurrenceThreshold = 2;

    public const string ReasonNoSignal = "mast.no_recurrent_mode";
    public const string ReasonSpecification = "mast.specification_recurrent";
    public const string ReasonMisalignment = "mast.misalignment_recurrent";
    public const string ReasonVerification = "mast.verification_recurrent";

    public static MastCorrection Evaluate(IReadOnlyList<MastObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var recurrent = observations
            .Where(observation => observation.Occurrences >= RecurrenceThreshold)
            .OrderByDescending(observation => observation.Occurrences)
            .ThenBy(observation => observation.FailureModeCode, StringComparer.Ordinal)
            .ToArray();
        if (recurrent.Length == 0)
        {
            return new MastCorrection(
                false, 0, false, false, ReasonNoSignal,
                "Nenhum modo de falha se repetiu o suficiente para virar padrão.");
        }

        var dominant = recurrent[0];
        var evidence =
            $"O modo '{dominant.FailureModeCode}' ({dominant.Category}) apareceu " +
            $"{dominant.Occurrences} vezes nas tentativas recentes deste projeto.";
        return dominant.Category switch
        {
            // O pedido estava ambíguo. Revisar mais fundo não conserta enunciado ruim: o revisor
            // vai reprovar de novo pelo mesmo motivo, e o custo dobra sem o defeito sair do lugar.
            MastCategory.SpecificationAndDesign => new MastCorrection(
                true, 0, PreferSmallerSlices: true, RequireExplicitAcceptanceCriteria: true,
                ReasonSpecification, evidence),

            // A coordenação falhou. Fatias menores e mais independentes reduzem a superfície de
            // colisão; profundidade de revisão não arbitra conflito entre agentes.
            MastCategory.InterAgentMisalignment => new MastCorrection(
                true, 0, PreferSmallerSlices: true, RequireExplicitAcceptanceCriteria: false,
                ReasonMisalignment, evidence),

            // O trabalho terminou sem ninguém conferir de verdade. Aqui — e só aqui — revisar mais
            // fundo é a correção certa.
            _ => new MastCorrection(
                true, ExtraReviewDepth: 1, PreferSmallerSlices: false,
                RequireExplicitAcceptanceCriteria: false, ReasonVerification, evidence),
        };
    }
}
