namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Uma onda de despacho. Ela carrega os NÍVEIS topológicos originais em ordem: reagrupar reduz a
/// quantidade de barreiras de coordenação, nunca a ordem entre cards dependentes.
/// </summary>
public sealed record PlanWaveGroup(IReadOnlyList<IReadOnlyList<string>> Levels)
{
    /// <summary>Todos os cards da onda, na ordem dos níveis.</summary>
    public IReadOnlyList<string> CardIds => Levels.SelectMany(level => level).ToArray();

    /// <summary>Verdadeiro quando a onda absorveu mais de um nível por reagrupamento.</summary>
    public bool IsRegrouped => Levels.Count > 1;
}

/// <summary>
/// Avaliação de profundidade de um plano. <see cref="Depth"/> é a profundidade topológica real;
/// <see cref="Groups"/> nunca excede <see cref="MaxDepth"/>.
/// </summary>
public sealed record PlanDepthAssessment(
    int Depth,
    int MaxDepth,
    bool ExceedsLimit,
    IReadOnlyList<PlanWaveGroup> Groups);

/// <summary>
/// Política de PROFUNDIDADE do plano (B4).
///
/// Uma cadeia de dependências funda é cara por um motivo específico: cada nível é uma barreira de
/// coordenação, e cada barreira faz agentes prontos ficarem parados esperando o nível inteiro
/// terminar. Um plano de sete níveis serializa sete vezes um trabalho que raramente precisa disso —
/// quase sempre a profundidade é artefato da decomposição, não da natureza da demanda.
///
/// O teto de 3–4 níveis não apaga dependências: ele limita quantas BARREIRAS o plano impõe.
/// Níveis excedentes são reagrupados em ondas, preservando a ordem entre eles, e o despacho real
/// não espera onda nenhuma — cada card sai assim que os SEUS provedores concluem
/// (<see cref="CardDependencyPlan.GetReadyCards"/>). A onda é a unidade de planejamento; a
/// prontidão individual é a unidade de despacho.
/// </summary>
public static class PlanDepthPolicy
{
    /// <summary>Teto padrão de barreiras de coordenação em um plano.</summary>
    public const int DefaultMaxDepth = 4;

    /// <summary>Piso admitido para o teto configurável (a faixa homologada é 3–4).</summary>
    public const int MinimumConfigurableDepth = 3;

    public const string ReasonDepthExceeded = "plan.depth_exceeded";

    public static PlanDepthAssessment Evaluate(
        IReadOnlyList<IReadOnlyList<string>> waves,
        int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(waves);
        if (maxDepth < MinimumConfigurableDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth),
                $"A profundidade máxima não pode ser menor que {MinimumConfigurableDepth}.");
        }

        var depth = waves.Count;
        if (depth <= maxDepth)
        {
            return new PlanDepthAssessment(
                depth,
                maxDepth,
                ExceedsLimit: false,
                waves.Select(wave => new PlanWaveGroup([wave])).ToArray());
        }

        // Distribuição uniforme dos níveis entre exatamente `maxDepth` ondas: determinística e sem
        // concentrar o excedente na última onda, que viraria um gargalo no fim do plano.
        var buckets = new List<List<IReadOnlyList<string>>>(maxDepth);
        for (var index = 0; index < maxDepth; index++)
        {
            buckets.Add([]);
        }

        for (var level = 0; level < depth; level++)
        {
            buckets[level * maxDepth / depth].Add(waves[level]);
        }

        return new PlanDepthAssessment(
            depth,
            maxDepth,
            ExceedsLimit: true,
            buckets.Select(bucket => new PlanWaveGroup(bucket)).ToArray());
    }

    public static PlanDepthAssessment Evaluate(CardDependencyPlan plan, int maxDepth = DefaultMaxDepth)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Evaluate(plan.DispatchWaves, maxDepth);
    }
}
