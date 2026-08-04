namespace Harness.Modules.Governance.Coordination;

/// <summary>
/// O escopo planejado de um card: os paths, o motivo e se houve estreitamento de fato.
/// <see cref="Narrowed"/> falso significa que o planejador não conseguiu concluir nada com
/// segurança e devolveu o escopo do PAPEL — o comportamento anterior, preservado de propósito.
/// </summary>
public sealed record CardPathScopePlan(
    IReadOnlyList<string> Claims,
    bool Narrowed,
    string ReasonCode,
    IReadOnlyList<string> MatchedSurfaces);

/// <summary>
/// Deriva o escopo de paths de UM CARD, em vez de dar a ele o escopo inteiro do papel.
///
/// O problema que isto resolve foi medido ao vivo: quatro cards de backend independentes do mesmo
/// projeto reivindicavam `src/**` cada um, três eram recusados por `ScopeConflict` a cada ciclo e
/// o paralelismo real do produto era de UM card por papel por projeto. A política de escopo já
/// aceitava claims estreitos; o que faltava era alguém capaz de decidir quais.
///
/// É PURO e determinístico: as mesmas entradas rendem sempre o mesmo escopo, para que o claim seja
/// auditável e reproduzível. E é CONSERVADOR por construção — na dúvida, devolve o escopo do
/// papel. Um claim estreito demais trava o agente no meio do trabalho, o que é pior do que um
/// claim amplo que apenas serializa.
/// </summary>
public static class CardPathScopePlanner
{
    /// <summary>
    /// Quantas superfícies um card pode reivindicar antes de o estreitamento perder sentido. Um
    /// card que toca cinco módulos não é um card estreito: é um card mal decomposto, e dar-lhe
    /// meia dúzia de claims só espalharia o conflito em vez de reduzi-lo.
    /// </summary>
    private const int MaximumSurfaces = 3;

    public static CardPathScopePlan Plan(
        IReadOnlyList<string> roleClaims,
        RepositorySurfaceMap surfaceMap,
        string title,
        string instruction)
    {
        ArgumentNullException.ThrowIfNull(roleClaims);
        ArgumentNullException.ThrowIfNull(surfaceMap);

        if (roleClaims.Count == 0)
        {
            return new CardPathScopePlan([], false, "scope.role_has_no_claims", []);
        }

        var matches = surfaceMap.Match($"{title}\n{instruction}");
        if (matches.Count == 0)
        {
            return new CardPathScopePlan(roleClaims, false, "scope.no_surface_matched", []);
        }

        if (matches.Count > MaximumSurfaces)
        {
            return new CardPathScopePlan(roleClaims, false, "scope.too_many_surfaces", []);
        }

        // Só valem os paths que o PAPEL já concede. O estreitamento reduz o alcance do card; ele
        // nunca pode servir de porta para alcançar o que o papel não permitia — senão o planejador
        // vira um caminho de escalada de privilégio disfarçado de otimização.
        var claims = matches
            .SelectMany(surface => surface.Paths)
            .Where(path => roleClaims.Any(role => IsWithin(path, role)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (claims.Length == 0)
        {
            return new CardPathScopePlan(roleClaims, false, "scope.surface_outside_role", []);
        }

        // O ESTREITAMENTO NÃO PODE TIRAR DO CARD O LUGAR ONDE ELE FOI MANDADO ESCREVER.
        //
        // Ele é otimização — reduz o alcance para que dois cards do mesmo papel rodem em paralelo
        // sem disputar arquivo. Quando a otimização corta justamente a superfície do entregável, o
        // card fica impedido de cumprir a própria instrução, e o sintoma é o pior possível: o
        // agente roda, gasta token e entrega diff VAZIO, porque escrever era impossível. Não há
        // erro, não há recusa visível, e o revisor reprova por "não fez o trabalho".
        //
        // Medido em 04/08: um card cuja instrução dizia "UM arquivo ADR em docs/decisions/"
        // recebeu como único claim `tools/backend/**`. Sete tentativas, sete diffs vazios, sete
        // reprovações corretas — sobre um trabalho que o sistema tinha tornado impossível.
        //
        // A regra: se a instrução NOMEIA um diretório de saída e o recorte não o cobre, desiste do
        // recorte e devolve o escopo inteiro do papel. Perder paralelismo é barato; perder a
        // capacidade de entregar não é. E a segurança não afrouxa — o papel continua sendo o teto.
        var declaredOutputs = DeclaredOutputPaths(instruction)
            .Where(path => roleClaims.Any(role => IsWithin(path, role)))
            .ToArray();
        var uncovered = declaredOutputs
            .Where(path => !claims.Any(claim => IsWithin(path, claim)))
            .ToArray();
        if (uncovered.Length > 0)
        {
            return new CardPathScopePlan(roleClaims, false, "scope.declared_output_uncovered", []);
        }

        return new CardPathScopePlan(
            claims, true, "scope.narrowed", [.. matches.Select(surface => surface.Name)]);
    }

    /// <summary>
    /// Diretórios que a instrução NOMEIA como destino do entregável.
    ///
    /// Deliberadamente conservador: reconhece só caminhos escritos de forma inequívoca — com barra
    /// e sem espaço —, porque um falso positivo aqui só custa paralelismo, enquanto um falso
    /// negativo devolve o card mudo que esta regra existe para impedir.
    /// </summary>
    private static IEnumerable<string> DeclaredOutputPaths(string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            yield break;
        }

        foreach (System.Text.RegularExpressions.Match match in OutputPathPattern.Matches(instruction))
        {
            yield return match.Value.TrimEnd('/');
        }
    }

    private static readonly System.Text.RegularExpressions.Regex OutputPathPattern =
        new(@"(?<![\w/.-])(?:src|tests|docs|infra|tools|frontend|governance)/[\w./-]*",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// O path candidato está contido no claim do papel? Compara pelas RAÍZES, tolerando o sufixo
    /// de varredura dos dois lados: `src/Modules/X/**` está dentro de `src/**`.
    /// </summary>
    private static bool IsWithin(string candidate, string roleClaim)
    {
        var path = Trim(candidate);
        var root = Trim(roleClaim);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        return normalized.EndsWith("/**", StringComparison.Ordinal)
            ? normalized[..^3].TrimEnd('/')
            : normalized;
    }
}
