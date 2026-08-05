using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Modules.Workflows.Product;

/// <summary>
/// Extrai decisões técnicas EXPLÍCITAS de um texto humano — pedido do usuário, corpo de um ADR
/// aprovado, constraint da organização.
///
/// A regra que governa este parser é a precisão, não a cobertura: ele só reconhece afirmação
/// inequívoca sobre uma área conhecida. Uma frase ambígua não vira diretiva — vira silêncio, e o
/// silêncio aplica o baseline. Inferir "PostgreSQL" de uma menção casual ao Postgres numa
/// justificativa criaria uma decisão que ninguém tomou, e decisão inventada é pior que decisão
/// ausente: ela tem aparência de aprovação.
/// </summary>
public static class ProfileDirectiveParser
{
    /// <summary>Tecnologias reconhecidas por área, com o valor canônico que entra no perfil.</summary>
    private static readonly (string Area, string[] Terms, string Value)[] Known =
    [
        (EffectiveProfileResolver.AreaFrontendFramework, ["angular"], "Angular"),
        (EffectiveProfileResolver.AreaFrontendFramework, ["react"], "React"),
        (EffectiveProfileResolver.AreaFrontendFramework, ["vue", "vue.js", "vuejs"], "Vue"),
        (EffectiveProfileResolver.AreaFrontendFramework, ["svelte"], "Svelte"),
        (EffectiveProfileResolver.AreaFrontendFramework, ["blazor"], "Blazor"),
        (EffectiveProfileResolver.AreaDatabase, ["postgresql", "postgres"], "PostgreSQL"),
        (EffectiveProfileResolver.AreaDatabase, ["sql server", "sqlserver", "mssql"], "SQL Server"),
        (EffectiveProfileResolver.AreaDatabase, ["mysql", "mariadb"], "MySQL"),
        (EffectiveProfileResolver.AreaDatabase, ["oracle"], "Oracle"),
        (EffectiveProfileResolver.AreaDatabase, ["sqlite"], "SQLite"),
        (EffectiveProfileResolver.AreaDatabase, ["mongodb", "mongo"], "MongoDB"),
        (EffectiveProfileResolver.AreaBackendRuntime, [".net 8", "dotnet 8", "net8"], ".NET 8"),
        (EffectiveProfileResolver.AreaBackendRuntime, [".net 9", "dotnet 9", "net9"], ".NET 9"),
        (EffectiveProfileResolver.AreaBackendRuntime, ["node.js", "nodejs"], "Node.js"),
        (EffectiveProfileResolver.AreaBackendRuntime, ["java 21", "java 17"], "Java"),
        (EffectiveProfileResolver.AreaArchitectureStyle, ["microsservi", "microservi"], "Microsserviços"),
        (EffectiveProfileResolver.AreaArchitectureStyle, ["monolito modular", "modular monolith"],
            "Modular Monolith + Clean Architecture"),
        (EffectiveProfileResolver.AreaFrontendLanguage, ["typescript"], "TypeScript"),
        (EffectiveProfileResolver.AreaFrontendLanguage, ["javascript"], "JavaScript"),
        (EffectiveProfileResolver.AreaHosting, ["azure"], "Azure"),
        (EffectiveProfileResolver.AreaHosting, ["aws"], "AWS"),
        (EffectiveProfileResolver.AreaHosting, ["on-premise", "on premise", "datacenter próprio"],
            "On-premise"),
    ];

    /// <summary>
    /// Verbos que transformam menção em DECISÃO. Sem um deles por perto, a tecnologia citada é
    /// contexto, não escolha — "avaliamos PostgreSQL e descartamos" não pode virar diretiva.
    /// </summary>
    private static readonly Regex DecisionVerb = new(
        @"\b(quero|queremos|usar|usaremos|utilizar|utilizaremos|deve\s+ser|devera|devera\s+ser|" +
        @"sera|adotar|adotaremos|padronizar|padronizado|migrar\s+para|trocar\s+para|em\s+vez\s+de|" +
        @"obrigatorio|exigido|decidimos|decidido|fica\s+definido|passa\s+a\s+ser|use|utilize|" +
        // "Oracle (requisito da TrensRJ)" é a forma natural de o dono declarar decisão fechada
        // vinda da organização — o gate do Prisma pegou o parser ignorando exatamente isso.
        @"requisito\s+d[aeo])\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Negações que invalidam a frase como decisão positiva.</summary>
    private static readonly Regex Negation = new(
        @"\b(nao|jamais|nunca|descartad|rejeitad|evitar|sem)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<ProfileDirective> Parse(
        string? text,
        ProfileAuthority authority,
        string sourceId,
        string sourceType,
        string? adrId = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var directives = new List<ProfileDirective>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Frase a frase: o verbo de decisão precisa estar PERTO da tecnologia. Varrer o texto
        // inteiro faria um verbo do primeiro parágrafo autorizar uma menção do último.
        foreach (var sentence in Split(text))
        {
            var normalized = Normalize(sentence);
            if (!DecisionVerb.IsMatch(normalized) || Negation.IsMatch(normalized))
            {
                continue;
            }

            foreach (var (area, terms, value) in Known)
            {
                if (seen.Contains(area) || !terms.Any(term =>
                        normalized.Contains(term, StringComparison.Ordinal)))
                {
                    continue;
                }

                seen.Add(area);
                directives.Add(new ProfileDirective(
                    authority, area, value,
                    $"{sourceType}:{sourceId} — \"{Shorten(sentence)}\"", adrId));
            }
        }

        return directives;
    }

    private static IEnumerable<string> Split(string text) => text
        .Split(['.', '\n', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(sentence => sentence.Length > 3);

    private static string Shorten(string sentence) =>
        sentence.Length <= 120 ? sentence.Trim() : sentence.Trim()[..117] + "…";

    private static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
