namespace Harness.Modules.Coordination.Application;

/// <summary>
/// As três categorias de falha de sistema multiagente. A divisão importa porque cada categoria
/// pede uma correção diferente: especificação se corrige reescrevendo o pedido, desalinhamento se
/// corrige mudando a coordenação, e verificação se corrige mudando o portão.
/// </summary>
public enum MastCategory
{
    /// <summary>Especificação e desenho do sistema: o pedido ou o papel estavam errados.</summary>
    SpecificationAndDesign = 1,

    /// <summary>Desalinhamento entre agentes: a coordenação falhou, não a execução.</summary>
    InterAgentMisalignment = 2,

    /// <summary>Verificação e encerramento: o trabalho terminou sem ninguém conferir de verdade.</summary>
    VerificationAndTermination = 3
}

/// <summary>Um modo de falha da taxonomia, com o que fazer a respeito dele.</summary>
public sealed record MastFailureMode(
    string Code,
    MastCategory Category,
    string BusinessDescription,
    string CorrectiveLever);

/// <summary>
/// Taxonomia MAST das falhas de sistema multiagente (B1) — 14 modos em 3 categorias.
///
/// Antes dela, o encerramento de uma tentativa produzia um veredito binário: passou ou falhou. Isso
/// basta para decidir se retenta, e não serve para mais nada. Duas tentativas que "falharam" podem
/// ter falhado por motivos opostos — uma porque o enunciado estava ambíguo, outra porque o agente
/// terminou antes de conferir — e a correção de uma é exatamente o contrário da correção da outra.
///
/// Classificar o modo transforma histórico em decisão: se a distribuição de uma demanda concentra
/// falhas de ESPECIFICAÇÃO, decompor mais não ajuda — o enunciado precisa mudar. Se concentra falhas
/// de VERIFICAÇÃO, aprofundar a revisão ajuda. Se concentra DESALINHAMENTO, o problema é o número de
/// agentes e a fronteira entre eles.
///
/// A classificação é feita por avaliador em contexto FRESCO no encerramento da tentativa: quem
/// executou não é bom juiz do próprio modo de falha, porque a explicação que ele daria é a mesma
/// história que o levou ao erro.
/// </summary>
public static class MastTaxonomy
{
    // --- Categoria 1: especificação e desenho ---
    public const string DisobeyTaskSpecification = "mast.1_1_disobey_task_specification";
    public const string DisobeyRoleSpecification = "mast.1_2_disobey_role_specification";
    public const string StepRepetition = "mast.1_3_step_repetition";
    public const string LossOfConversationHistory = "mast.1_4_loss_of_conversation_history";
    public const string UnawareOfTerminationConditions = "mast.1_5_unaware_of_termination_conditions";

    // --- Categoria 2: desalinhamento entre agentes ---
    public const string ConversationReset = "mast.2_1_conversation_reset";
    public const string FailToAskForClarification = "mast.2_2_fail_to_ask_for_clarification";
    public const string TaskDerailment = "mast.2_3_task_derailment";
    public const string InformationWithholding = "mast.2_4_information_withholding";
    public const string IgnoredOtherAgentInput = "mast.2_5_ignored_other_agent_input";
    public const string ReasoningActionMismatch = "mast.2_6_reasoning_action_mismatch";

    // --- Categoria 3: verificação e encerramento ---
    public const string PrematureTermination = "mast.3_1_premature_termination";
    public const string IncompleteVerification = "mast.3_2_incomplete_verification";
    public const string IncorrectVerification = "mast.3_3_incorrect_verification";

    private static readonly MastFailureMode[] Modes =
    [
        new(DisobeyTaskSpecification, MastCategory.SpecificationAndDesign,
            "A tarefa foi feita de um jeito diferente do que estava pedido.",
            "Reescrever o enunciado com o critério de aceite explícito."),
        new(DisobeyRoleSpecification, MastCategory.SpecificationAndDesign,
            "A pessoa saiu do papel que ela tinha nesta tarefa.",
            "Estreitar a competência declarada e a fronteira do card."),
        new(StepRepetition, MastCategory.SpecificationAndDesign,
            "O mesmo passo foi repetido sem avançar o trabalho.",
            "Marcar o progresso no checkpoint e cortar o passo redundante do plano."),
        new(LossOfConversationHistory, MastCategory.SpecificationAndDesign,
            "O contexto do que já havia sido combinado foi perdido no meio.",
            "Reduzir o contexto ao essencial e fixá-lo no bundle da tentativa."),
        new(UnawareOfTerminationConditions, MastCategory.SpecificationAndDesign,
            "Não estava claro quando a tarefa deveria ser considerada pronta.",
            "Declarar a condição de pronto no próprio card."),

        new(ConversationReset, MastCategory.InterAgentMisalignment,
            "A conversa entre as pessoas da equipe começou do zero e perdeu o combinado.",
            "Persistir o estado do turno e retomar do checkpoint em vez de reiniciar."),
        new(FailToAskForClarification, MastCategory.InterAgentMisalignment,
            "Havia uma dúvida real e ninguém parou para perguntar.",
            "Facilitar a escalação: perguntar precisa ser mais barato que adivinhar."),
        new(TaskDerailment, MastCategory.InterAgentMisalignment,
            "O trabalho foi desviado para outro assunto no caminho.",
            "Fronteira negativa no card, nomeando o que é de outra tarefa."),
        new(InformationWithholding, MastCategory.InterAgentMisalignment,
            "Alguém tinha uma informação necessária e ela não chegou a quem precisava.",
            "Tornar o achado um artefato do card, não uma observação de turno."),
        new(IgnoredOtherAgentInput, MastCategory.InterAgentMisalignment,
            "A contribuição de um colega foi ignorada.",
            "Exigir resposta explícita ao apontamento antes de submeter."),
        new(ReasoningActionMismatch, MastCategory.InterAgentMisalignment,
            "O que foi explicado não corresponde ao que foi feito de fato.",
            "Comparar o diff com o relato no portão de comportamento."),

        new(PrematureTermination, MastCategory.VerificationAndTermination,
            "A tarefa foi encerrada antes de estar realmente pronta.",
            "Condição de pronto verificável e camada de comportamento no portão."),
        new(IncompleteVerification, MastCategory.VerificationAndTermination,
            "A conferência foi parcial: parte do trabalho não foi checada.",
            "Camada determinística obrigatória antes de ocupar o revisor."),
        new(IncorrectVerification, MastCategory.VerificationAndTermination,
            "A conferência aprovou algo que não deveria ter passado.",
            "Revisão de risco alto em modelo mais capaz e revisor distinto do executor.")
    ];

    public static IReadOnlyList<MastFailureMode> All => Modes;

    /// <summary>Exatamente 14 modos: a taxonomia é fechada, e um modo novo é decisão de arquitetura.</summary>
    public const int ExpectedModeCount = 14;

    public static MastFailureMode? Find(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Modes.FirstOrDefault(mode => string.Equals(mode.Code, code.Trim(), StringComparison.Ordinal));

    public static IReadOnlyList<MastFailureMode> ByCategory(MastCategory category) =>
        Modes.Where(mode => mode.Category == category).ToArray();
}

/// <summary>Quantas tentativas caíram em cada categoria — a leitura que orienta a decisão.</summary>
public sealed record MastDistribution(IReadOnlyDictionary<MastCategory, int> CountByCategory)
{
    public int Total => CountByCategory.Values.Sum();

    /// <summary>Categoria dominante, ou nula quando não há falhas ou há empate.</summary>
    public MastCategory? Dominant()
    {
        if (Total == 0)
        {
            return null;
        }

        var ordered = CountByCategory
            .Where(pair => pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .ToArray();

        // Empate não tem dominante: escolher um dos dois por desempate arbitrário faria a Bruna
        // agir com convicção sobre uma leitura que os dados não sustentam.
        return ordered.Length >= 2 && ordered[0].Value == ordered[1].Value
            ? null
            : ordered[0].Key;
    }

    public static MastDistribution From(IEnumerable<string> failureModeCodes)
    {
        ArgumentNullException.ThrowIfNull(failureModeCodes);
        var counts = Enum.GetValues<MastCategory>().ToDictionary(category => category, _ => 0);
        foreach (var code in failureModeCodes)
        {
            var mode = MastTaxonomy.Find(code);
            if (mode is not null)
            {
                counts[mode.Category]++;
            }
        }

        return new MastDistribution(counts);
    }
}

/// <summary>
/// O que a distribuição recomenda. Não é ordem: é a leitura que a Bruna usa ao decidir decomposição,
/// especialista e profundidade de revisão.
/// </summary>
public static class MastAdvice
{
    public const string RewriteSpecification = "mast.advice.rewrite_specification";
    public const string ReduceParallelism = "mast.advice.reduce_parallelism";
    public const string DeepenVerification = "mast.advice.deepen_verification";
    public const string NoSignal = "mast.advice.no_signal";

    /// <summary>
    /// Decompor mais é a reação instintiva a qualquer falha, e é errada em dois dos três casos:
    /// contra falha de especificação ela multiplica o enunciado ambíguo, e contra desalinhamento ela
    /// aumenta justamente o número de fronteiras que já estava falhando.
    /// </summary>
    public static string Recommend(MastDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        return distribution.Dominant() switch
        {
            MastCategory.SpecificationAndDesign => RewriteSpecification,
            MastCategory.InterAgentMisalignment => ReduceParallelism,
            MastCategory.VerificationAndTermination => DeepenVerification,
            _ => NoSignal
        };
    }
}
