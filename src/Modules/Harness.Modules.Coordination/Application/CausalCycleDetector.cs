namespace Harness.Modules.Coordination.Application;

/// <summary>
/// Uma aresta causal lida do ledger: <see cref="CauseKey"/> GEROU <see cref="EffectKey"/>.
/// As chaves são opacas e estáveis (ex.: <c>demand:01H…</c>, <c>plan:01H…</c>, <c>card:01H…</c>) —
/// o detector não interpreta o que elas significam.
/// </summary>
public sealed record CausalEdge(string CauseKey, string EffectKey);

/// <summary>Ciclo causal encontrado, com o caminho fechado na ordem em que se formou.</summary>
public sealed record CausalCycle(IReadOnlyList<string> Path)
{
    public string Describe() => string.Join(" -> ", Path);
}

/// <summary>
/// Detector de CICLO CAUSAL sobre o ledger (B9).
///
/// O laço que interessa aqui não é o de repetição — é o de causalidade: A gerou B, e B regenerou A.
/// Um teto de turnos ou uma taxa por janela apenas ADIAM esse laço, porque cada volta parece
/// trabalho novo e legítimo vista isoladamente. Só olhando a corrente de causa e efeito registrada
/// no ledger o laço aparece como o que é.
///
/// O detector é puro e trabalha sobre as arestas já materializadas: ele responde se acrescentar
/// "causa gerou efeito" FECHARIA um ciclo (isto é, se o efeito já alcança a causa) e devolve o
/// caminho — a evidência que vai no evento auditado, porque interromper sem dizer por quê é
/// indistinguível de travar.
/// </summary>
public static class CausalCycleDetector
{
    /// <summary>
    /// Verdadeiro quando acrescentar a aresta <paramref name="causeKey"/> → <paramref name="effectKey"/>
    /// fecha um ciclo. Devolve o caminho fechado quando fecha.
    /// </summary>
    public static CausalCycle? DetectClosingCycle(
        IReadOnlyCollection<CausalEdge> edges,
        string causeKey,
        string effectKey)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentException.ThrowIfNullOrWhiteSpace(causeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectKey);

        var cause = causeKey.Trim();
        var effect = effectKey.Trim();

        // Auto-geração é o ciclo mais curto possível e também o mais fácil de passar despercebido.
        if (string.Equals(cause, effect, StringComparison.Ordinal))
        {
            return new CausalCycle([cause, effect]);
        }

        var path = FindPath(BuildAdjacency(edges), effect, cause);
        if (path is null)
        {
            return null;
        }

        // O caminho vai do efeito de volta à causa; fechá-lo com a aresta nova completa o ciclo.
        return new CausalCycle([cause, .. path]);
    }

    /// <summary>Procura qualquer ciclo já materializado nas arestas do ledger.</summary>
    public static CausalCycle? DetectExistingCycle(IReadOnlyCollection<CausalEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var adjacency = BuildAdjacency(edges);

        foreach (var origin in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var next in adjacency[origin])
            {
                if (string.Equals(next, origin, StringComparison.Ordinal))
                {
                    return new CausalCycle([origin, origin]);
                }

                var path = FindPath(adjacency, next, origin);
                if (path is not null)
                {
                    return new CausalCycle([origin, .. path]);
                }
            }
        }

        return null;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(IReadOnlyCollection<CausalEdge> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.CauseKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(edge.EffectKey);

            var cause = edge.CauseKey.Trim();
            var effect = edge.EffectKey.Trim();
            if (!adjacency.TryGetValue(cause, out var effects))
            {
                effects = [];
                adjacency[cause] = effects;
            }

            if (!effects.Contains(effect, StringComparer.Ordinal))
            {
                effects.Add(effect);
            }
        }

        foreach (var effects in adjacency.Values)
        {
            effects.Sort(StringComparer.Ordinal);
        }

        return adjacency;
    }

    /// <summary>Busca em largura: o caminho mais curto é o mais legível no evento auditado.</summary>
    private static List<string>? FindPath(
        Dictionary<string, List<string>> adjacency,
        string from,
        string to)
    {
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, to, StringComparison.Ordinal))
            {
                var path = new List<string> { current };
                while (previous.TryGetValue(current, out var parent))
                {
                    path.Add(parent);
                    current = parent;
                }

                path.Reverse();
                return path;
            }

            if (!adjacency.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var candidate in next)
            {
                if (visited.Add(candidate))
                {
                    previous[candidate] = current;
                    queue.Enqueue(candidate);
                }
            }
        }

        return null;
    }
}
