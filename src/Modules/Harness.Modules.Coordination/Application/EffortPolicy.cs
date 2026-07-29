namespace Harness.Modules.Coordination.Application;

/// <summary>Natureza do trabalho — o que a tarefa exige de quem a executa.</summary>
public enum WorkNature
{
    /// <summary>Mudança visual ou de texto: verificável olhando, barata de refazer.</summary>
    Visual = 0,

    /// <summary>Implementação comum, com critério de aceite claro.</summary>
    Implementation = 1,

    /// <summary>Mexe em contrato, dado persistido ou fronteira entre módulos.</summary>
    Structural = 2,

    /// <summary>Investigação: o resultado esperado é conhecimento, não código.</summary>
    Investigation = 3
}

/// <summary>Faixa de risco do card.</summary>
public enum RiskTier
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

/// <summary>
/// Orçamento de esforço de um card. Determinístico: a mesma entrada devolve sempre isto.
/// </summary>
public sealed record EffortBudget(
    int Agents,
    int TokenBudget,
    int MaxRounds,
    int ReviewDepth,
    bool FanOutAllowed,
    string ReasonCode);

/// <summary>Uma direção candidata a paralelizar.</summary>
public sealed record FanOutDirection(
    string Key,
    IReadOnlyList<string> TouchedScopes,
    bool CarriesValue);

/// <summary>
/// Política de ESFORÇO (B3) — determinística por tipo × risco × natureza.
///
/// O problema que ela resolve não é escassez, é desperdício com cara de zelo. Sem uma regra, a
/// tendência de um orquestrador é sempre a mesma: mandar mais agentes, pedir mais rodadas, revisar
/// mais fundo — como se esforço fosse gratuito e proporcional à qualidade. Não é. Três agentes numa
/// troca de texto produzem três versões da mesma linha e um conflito; cinco rodadas num card com
/// critério de aceite claro só descobrem que ele já estava pronto na segunda.
///
/// Determinismo é requisito, não elegância: se a mesma demanda receber orçamentos diferentes em
/// dias diferentes, ninguém consegue dizer se o resultado melhorou pelo plano ou pela sorte.
///
/// A regra de FAN-OUT é onde mais se erra. Paralelizar só ajuda quando as direções são
/// independentes E cada uma carrega valor próprio. Direções que tocam o mesmo escopo não são
/// paralelas: são um conflito agendado. E demanda pequena ou visual é um agente — dividir o que já
/// é pequeno multiplica coordenação sem multiplicar entrega.
/// </summary>
public static class EffortPolicy
{
    public const string ReasonVisualSingleAgent = "effort.visual_single_agent";
    public const string ReasonSmallSingleAgent = "effort.small_single_agent";
    public const string ReasonStandard = "effort.standard";
    public const string ReasonStructuralReinforced = "effort.structural_reinforced";
    public const string ReasonCriticalMaximum = "effort.critical_maximum";
    public const string ReasonInvestigation = "effort.investigation";

    /// <summary>
    /// Orçamento do card. <paramref name="isSmall"/> é o fato objetivo "cabe numa rodada"
    /// (poucos arquivos, sem contrato novo), decidido pelo planner e não estimado aqui.
    /// </summary>
    public static EffortBudget Decide(WorkNature nature, RiskTier risk, bool isSmall = false)
    {
        if (!Enum.IsDefined(nature))
        {
            throw new ArgumentOutOfRangeException(nameof(nature));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        // Visual é o caso mais fácil de superdimensionar e o que menos ganha com isso: o resultado
        // se confere olhando, e refazer custa pouco.
        if (nature == WorkNature.Visual)
        {
            return new EffortBudget(
                Agents: 1,
                TokenBudget: 60_000,
                MaxRounds: 2,
                ReviewDepth: risk >= RiskTier.High ? 1 : 0,
                FanOutAllowed: false,
                ReasonVisualSingleAgent);
        }

        // Investigação entrega conhecimento. Mais agentes produzem relatórios divergentes sobre a
        // mesma pergunta, e revisar uma investigação é discutir opinião — o que vale é o teto.
        if (nature == WorkNature.Investigation)
        {
            return new EffortBudget(1, 120_000, 3, 0, FanOutAllowed: false, ReasonInvestigation);
        }

        // Pequeno é pequeno em qualquer risco: dividir o indivisível só agenda conflito.
        if (isSmall && risk <= RiskTier.Medium)
        {
            return new EffortBudget(1, 80_000, 2, 1, FanOutAllowed: false, ReasonSmallSingleAgent);
        }

        return (nature, risk) switch
        {
            (WorkNature.Structural, RiskTier.Critical) => new EffortBudget(
                Agents: 2, TokenBudget: 400_000, MaxRounds: 4, ReviewDepth: 3,
                FanOutAllowed: true, ReasonCriticalMaximum),
            (WorkNature.Structural, RiskTier.High) => new EffortBudget(
                2, 300_000, 3, 2, true, ReasonStructuralReinforced),
            (WorkNature.Structural, _) => new EffortBudget(
                1, 200_000, 3, 2, true, ReasonStructuralReinforced),
            (_, RiskTier.Critical) => new EffortBudget(
                2, 300_000, 4, 3, true, ReasonCriticalMaximum),
            (_, RiskTier.High) => new EffortBudget(
                1, 200_000, 3, 2, true, ReasonStandard),
            _ => new EffortBudget(1, 150_000, 3, 1, true, ReasonStandard)
        };
    }

    /// <summary>
    /// Direções que valem paralelizar: independentes entre si (nenhum escopo em comum) E com valor
    /// próprio. Devolve lista vazia quando o fan-out não se justifica — e vazio aqui significa
    /// "um agente", não "erro".
    /// </summary>
    public static IReadOnlyList<FanOutDirection> SelectFanOut(
        IReadOnlyList<FanOutDirection> candidates,
        EffortBudget budget)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.FanOutAllowed || budget.Agents <= 1)
        {
            return [];
        }

        var valuable = candidates
            .Where(direction => direction.CarriesValue)
            .OrderBy(direction => direction.Key, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<FanOutDirection>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var direction in valuable)
        {
            if (selected.Count >= budget.Agents)
            {
                break;
            }

            // Escopo em comum não é paralelismo: é conflito agendado. A direção fica para depois.
            if (direction.TouchedScopes.Any(claimed.Contains))
            {
                continue;
            }

            selected.Add(direction);
            foreach (var scope in direction.TouchedScopes)
            {
                claimed.Add(scope);
            }
        }

        // Uma direção sozinha não é fan-out: é o trabalho normal de um agente.
        return selected.Count >= 2 ? selected : [];
    }

    /// <summary>
    /// Assimetria de modelo (B3): o trabalho comum vai no modelo barato; a revisão de risco alto e
    /// o replanejamento vão no mais capaz. Errar para o barato numa revisão crítica é economizar
    /// no único lugar onde o erro passa direto.
    /// </summary>
    public static ModelTier RouteModel(RiskTier risk, bool isReview)
    {
        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        if (isReview)
        {
            return risk >= RiskTier.High ? ModelTier.Strong : ModelTier.Balanced;
        }

        return risk >= RiskTier.Critical ? ModelTier.Balanced : ModelTier.Economical;
    }
}

/// <summary>Faixa de capacidade do modelo, sem nomear provedor (o léxico proíbe no Negócio).</summary>
public enum ModelTier
{
    Economical = 0,
    Balanced = 1,
    Strong = 2
}
