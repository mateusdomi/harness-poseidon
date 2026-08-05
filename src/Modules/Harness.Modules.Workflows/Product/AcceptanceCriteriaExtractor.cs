using System.Text.RegularExpressions;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Extrai critérios de aceite de um documento de fonte SEM depender de template (Dual Project
/// Gate, Parte F): a seção é encontrada pelo TÍTULO semântico ("critérios de aceite",
/// "acceptance"), nunca pelo número; cada item vira um requisito tipado com ID ESTÁVEL —
/// marcador explícito quando existe (T14, AC-003), hash de conteúdo quando não. Renumerar,
/// retitular ou reordenar seções não muda um único ID.
/// </summary>
public static class AcceptanceCriteriaExtractor
{
    private static readonly Regex SectionTitle = new(
        @"crit[eé]rios?\s+de\s+aceite|acceptance\s+criteria|crit[eé]rios?\s+de\s+aceita[cç][aã]o",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex HeadingLine = new(
        @"^(#{1,4}\s+|\d{1,3}(\.\d+)*[.)—-]\s+|[A-ZÀ-Ú][A-ZÀ-Ú \d]{6,}$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex ItemLine = new(
        @"^\s*(?:[-*•]\s*)?(?:\*\*(?<marker>[A-Z]{1,3}-?\d{1,4})\*\*|(?<marker2>[A-Z]{2,3}-\d{1,4})\b|(?<number>\d{1,3})[.)])?\s*(?<text>\S.{9,})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Todos os critérios do documento inteiro. Sem seção de aceite reconhecível, devolve vazio
    /// — ausência declarada, nunca critérios inventados de outra seção.
    /// </summary>
    public static IReadOnlyList<SourceStatement> Extract(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = document.Split('\n');
        var statements = new List<SourceStatement>();
        var inSection = false;
        var sectionTitle = string.Empty;
        var fenced = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            var isHeading = HeadingLine.IsMatch(line.Trim()) && line.Trim().Length <= 120;
            if (isHeading)
            {
                if (SectionTitle.IsMatch(line))
                {
                    inSection = true;
                    sectionTitle = line.Trim();
                    continue;
                }

                // Título novo que NÃO é de aceite encerra a seção — exceto subtítulos internos
                // ("16.3 ITRC"), que continuam dentro dela.
                if (inSection && !IsSubHeadingOf(line.Trim()))
                {
                    inSection = false;
                }
            }

            if (!inSection || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = ItemLine.Match(line);
            if (!match.Success || !LooksLikeCriterion(line))
            {
                continue;
            }

            var text = match.Groups["text"].Value.Trim();
            var marker = match.Groups["marker"].Success ? match.Groups["marker"].Value
                : match.Groups["marker2"].Success ? match.Groups["marker2"].Value
                : null;
            var id = marker is { Length: > 0 }
                ? $"ac-{marker.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal)}"
                : $"ac-{SourceKnowledgeClassifier.StableId(text)[3..]}";
            statements.Add(new SourceStatement(
                id,
                KnowledgeCategory.AcceptanceCriterion,
                KnowledgeBinding.Required,
                text,
                sectionTitle,
                1.0));
        }

        // Dedup por id (o mesmo documento anexado com repetições não duplica critério).
        return [.. statements
            .GroupBy(statement => statement.Id, StringComparer.Ordinal)
            .Select(group => group.First())];
    }

    /// <summary>Subtítulo interno de uma seção de aceite: "16.3 ITRC", "### 16.4 Perfis".</summary>
    private static bool IsSubHeadingOf(string line) =>
        Regex.IsMatch(line, @"^(#{3,4}\s+)?\d{1,3}\.\d+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Um item de critério tem forma de exigência verificável. O formato NÃO é contrato: o
    /// Prisma escreve "- **T14** ..."; o levantamento de Indicadores escreve cláusulas puras
    /// terminadas em ponto e vírgula ("O sistema se conectar ao Oracle;"). Aceita-se: marcador
    /// explícito, cláusula terminada em ';', a forma "For possível …", ou verbo de exigência.
    /// Linha introdutória (termina em ':') e prosa de formato ficam fora.
    /// </summary>
    private static bool LooksLikeCriterion(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.EndsWith(':') ||
            trimmed.StartsWith("Formato", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.EndsWith(';') ||
            trimmed.StartsWith("For possível", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(
            trimmed,
            @"(\*\*[A-Z]{1,3}-?\d{1,4}\*\*|\b[A-Z]{2,3}-\d{1,4}\b|→|->|\bdeve\b|\bnegad|\bbloquead|\bconta\b|\bgera\b|\bpermite\b|\bexibe\b|\bvalida\b|\bimporta\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
