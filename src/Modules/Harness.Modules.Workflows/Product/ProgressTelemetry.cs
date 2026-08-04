namespace Harness.Modules.Workflows.Product;

/// <summary>
/// O que mudou entre a avaliação anterior e esta — medido, nunca julgado.
/// </summary>
/// <param name="Before">Lacunas na avaliação anterior. Zero quando esta é a primeira.</param>
/// <param name="After">Lacunas nesta avaliação.</param>
/// <param name="Resolved">Lacunas que existiam e sumiram.</param>
/// <param name="Repeated">Lacunas que existiam e continuam — a mesma parede, de novo.</param>
/// <param name="New">Lacunas que não existiam e apareceram.</param>
/// <param name="GateFailures">Achados que reprovaram o portão nesta avaliação.</param>
public sealed record ProgressDelta(
    int Before,
    int After,
    int Resolved,
    int Repeated,
    int New,
    int GateFailures,
    IReadOnlyList<string> ResolvedKeys,
    IReadOnlyList<string> RepeatedKeys,
    IReadOnlyList<string> NewKeys)
{
    /// <summary>Primeira avaliação: não há com o que comparar, e fingir que há seria inventar dado.</summary>
    public bool IsBaseline => Before == 0 && Resolved == 0 && Repeated == 0;

    /// <summary>
    /// O SALDO da tentativa. Positivo é lacuna a menos; zero com <see cref="Repeated"/> alto é a
    /// assinatura de 8 → 8 → 8, e é exatamente o que não dava para ver sem reconstruir o ledger à
    /// mão.
    /// </summary>
    public int Score => Resolved - New;

    public string Summary() =>
        $"before={Before} after={After} resolved={Resolved} repeated={Repeated} new={New} " +
        $"gate_failures={GateFailures} score={Score}";
}

/// <summary>
/// A medição de progresso entre tentativas.
///
/// <b>O que isto NÃO faz, e não deve fazer nesta fase:</b> não bloqueia, não prioriza, não
/// replaneja e não escala. É observabilidade pura, e a decisão de mantê-la assim é deliberada — a
/// política de "sem progresso" precisa ser desenhada sobre comportamento REAL observado num Golden
/// Run, não sobre a intuição de quem escreveu o medidor.
///
/// Por que ela existe agora: o único sinal de progresso que a plataforma tinha era
/// <c>OutputTokens &gt; 0</c> — "o modelo escreveu alguma coisa". Por esse critério, uma tentativa
/// que resolveu seis dos oito problemas e uma que bateu na mesma parede pela terceira vez são
/// idênticas. Sem distinguir
///
/// <code>8 → 5 → 2</code> de <code>8 → 8 → 8</code>
///
/// não há como uma fábrica aprender coisa alguma sobre a própria eficácia.
/// </summary>
public static class ProgressTelemetry
{
    /// <summary>
    /// Compara duas avaliações pelas LACUNAS, não pelos textos. A chave é
    /// <c>tipo:natureza-da-lacuna</c> — é o par que identifica "o mesmo problema", enquanto a
    /// justificativa muda de redação a cada execução e faria toda repetição parecer novidade.
    /// </summary>
    public static ProgressDelta Compare(
        IReadOnlyList<ProductEvidenceFinding>? previous,
        IReadOnlyList<ProductEvidenceFinding>? current)
    {
        var before = Keys(previous);
        var after = Keys(current);

        var resolved = before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var repeated = before.Intersect(after, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fresh = after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        return new ProgressDelta(
            before.Count,
            after.Count,
            resolved.Length,
            repeated.Length,
            fresh.Length,
            after.Count,
            resolved,
            repeated,
            fresh);
    }

    private static HashSet<string> Keys(IReadOnlyList<ProductEvidenceFinding>? findings) =>
        findings is null
            ? []
            : [.. findings.Select(finding =>
                $"{finding.Kind.ToString().ToLowerInvariant()}:{finding.Gap.ToString().ToLowerInvariant()}")];
}
